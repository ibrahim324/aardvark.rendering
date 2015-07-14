﻿namespace Aardvark.Rendering.GL.Compiler

open System
open System.Collections.Generic

open Aardvark.Base.Incremental
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Rendering.GL

type SortedProgram<'f when 'f :> IDynamicFragment<'f> and 'f : null>
        (newHandler : unit -> IFragmentHandler<'f>, 
         order : Order,
         newSorter : unit -> ISorter,
         manager : ResourceManager, 
         addInput : IAdaptiveObject -> unit, 
         removeInput : IAdaptiveObject -> unit) =
    
    let currentContext = Mod.init (match ContextHandle.Current with | Some ctx -> ctx | None -> null)
    let handler = newHandler()
    let changeSet = ChangeSet(addInput, removeInput)
    let resourceSet = ResourceSet(addInput, removeInput)
    let statistics = Mod.init FrameStatistics.Zero

    let ctx = { statistics = statistics; handler = handler; manager = manager; currentContext = currentContext; resourceSet = resourceSet }

    let mutable currentId = 0
    let idCache = Cache(Ag.emptyScope, fun m -> System.Threading.Interlocked.Increment &currentId)

    let sorter = newSorter()
    
    let fragments = Dict<RenderJob, UnoptimizedRenderJobFragment<'f>>()
    let sortedRenderJobs = sorter.SortedList
    do addInput sortedRenderJobs

    let mutable prolog = new UnoptimizedRenderJobFragment<'f>(handler.CreateProlog(), ctx)
    let mutable epilog = new UnoptimizedRenderJobFragment<'f>(handler.CreateEpilog(), ctx)
    let mutable run = handler.Compile ()



    member x.Dispose() =
        run <- fun _ -> failwith "cannot run disposed program"

        for (KeyValue(_,f)) in fragments do
            changeSet.Unlisten f.Changer
            f.Dispose()

        fragments.Clear()
        
        handler.Dispose()
        idCache.Clear(ignore)

        handler.Delete prolog.Fragment
        handler.Delete epilog.Fragment
        prolog <- null
        epilog <- null

    member x.Add (unsorted : RenderJob) =
        let rj = unsorted |> sorter.ToSortedRenderJob order
        sorter.Add rj

        // create a new RenderJobFragment and link it
        let fragment = new UnoptimizedRenderJobFragment<'f>(rj, ctx)
        fragments.[rj] <- fragment
        
        // listen to changes
        changeSet.Listen fragment.Changer

    member x.Remove (rj : RenderJob) =
        match fragments.TryRemove rj with
            | (true, f) ->
                sorter.Remove rj

                // detach the fragment
                f.Prev.Next <- f.Next
                f.Next.Prev <- f.Prev
                
                // no longer listen for changes
                changeSet.Unlisten f.Changer

                // finally dispose the fragment
                f.Dispose()

            | _ ->
                failwithf "cannot remove unknown renderjob: %A" rj

    member x.Run(fbo : Framebuffer, ctx : ContextHandle) =
        // change the current context if necessary
        if ctx <> currentContext.UnsafeCache then
            transact (fun () -> Mod.change currentContext ctx)

        let applySorting =
            async {
                let sorted = sortedRenderJobs |> Mod.force

                let mutable prev = prolog
                for rj in sorted do
                    match fragments.TryGetValue rj with
                        | (true, f) ->
                            prev.Next <- f
                            f.Prev <- prev
                            prev <- f
                        | _ ->
                            Log.warn "sorter returned unknown renderjob"

                prev.Next <- epilog
                epilog.Prev <- prev
            } |> Async.StartAsTask


        // update resources and instructions
        resourceSet.Update()
        changeSet.Update()

        // wait for the sorting
        applySorting.Wait()

        // run everything
        run prolog.Fragment

        statistics |> Mod.force |> handler.AdjustStatistics

    interface IDisposable with
        member x.Dispose() = x.Dispose()

    interface IProgram with
        member x.Add rj = x.Add rj
        member x.Remove rj = x.Remove rj
        member x.Run (fbo, ctx) = x.Run(fbo, ctx)
        member x.Update rj = failwith "not implemented"

