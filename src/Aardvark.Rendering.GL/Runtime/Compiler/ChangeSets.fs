﻿namespace Aardvark.Rendering.GL.Compiler

open System
open System.Collections.Generic

open Aardvark.Base.Incremental
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Rendering.GL

type ChangeSet(addInput : IAdaptiveObject -> unit, removeInput : IAdaptiveObject -> unit) =
    let l = obj()
    let all = HashSet<IMod<FrameStatistics>>()
    let set = HashSet<IMod<FrameStatistics>>()
    let callbacks = Dictionary<IMod<FrameStatistics>, IDisposable>()
    let sw = System.Diagnostics.Stopwatch()

    let dirty (m : IMod<FrameStatistics>) () =
        lock l (fun () -> 
            match callbacks.TryGetValue m with
                | (true, d) -> 
                    d.Dispose()
                    callbacks.Remove m |> ignore
                | _ -> ()

            set.Add m |> ignore
        )

    member x.Listen(m : IMod<FrameStatistics>) =
        if not m.IsConstant then
            lock m (fun () -> 
                lock l (fun () ->
                    if all.Add m then addInput m

                    if m.OutOfDate then
                        set.Add m |> ignore
                    else
                        let cb = dirty m
                        callbacks.[m] <- m.AddVolatileMarkingCallback cb
                )
            )
        else
            m |> Mod.force |> ignore

    member x.Unlisten (m : IMod<FrameStatistics>) =
        if not m.IsConstant then
            lock m (fun () ->
                lock l (fun () ->
                    if all.Remove m then removeInput m

                    set.Remove m |> ignore
                    match callbacks.TryGetValue m with
                        | (true, d) -> 
                            d.Dispose()
                            callbacks.Remove m |> ignore
                        | _ -> ()
                )
            )

    member x.Update() =
        let dirtySet = 
            lock l (fun () ->
                let dirtySet = set |> Seq.toArray
                set.Clear()

                for d in dirtySet do
                    let cb = dirty d
                    callbacks.[d] <- d.AddVolatileMarkingCallback cb

                dirtySet
            )

        sw.Restart()
        let mutable count = 0
        let mutable resTime = FrameStatistics.Zero
        for d in dirtySet do
            lock d (fun () ->
                resTime <- resTime + ( d |> Mod.force )
                count <- count + 1
            )
        sw.Stop()
        count, sw.Elapsed, resTime

type ResourceSet(addInput : IAdaptiveObject -> unit, removeInput : IAdaptiveObject -> unit) =
    let l = obj()
    let all = ReferenceCountingSet<IChangeableResource>()
    let set = HashSet<IChangeableResource>()
    let callbacks = Dictionary<IChangeableResource, IDisposable>()
    let sw = System.Diagnostics.Stopwatch()

    let dirty (m : IChangeableResource) () =
        lock l (fun () -> 
            match callbacks.TryGetValue m with
                | (true, d) -> 
                    d.Dispose()
                    callbacks.Remove m |> ignore
                | _ -> ()
            set.Add m |> ignore
        )

    member x.Resources = all

    member x.Listen(m : IChangeableResource) =
        lock m (fun () -> 
            lock l (fun () ->
                if all.Add m then
                    addInput m
                    if m.OutOfDate then
                        set.Add m |> ignore
                    else
                        let cb = dirty m
                        callbacks.[m] <- m.AddVolatileMarkingCallback cb
            )
        )

    member x.Unlisten (m : IChangeableResource) =
        lock m (fun () ->
            lock l (fun () ->
                if all.Remove m then
                    removeInput m
                    set.Remove m |> ignore
                    match callbacks.TryGetValue m with
                        | (true, d) -> 
                            d.Dispose()
                            callbacks.Remove m |> ignore
                        | _ -> ()
            )
        )

    member x.Update() =
        let dirtyResoruces = 
            lock l (fun () ->
                
                let dirtySet = set |> Seq.toArray
                set.Clear()

                for d in dirtySet do
                    let cb = dirty d
                    callbacks.[d] <- d.AddVolatileMarkingCallback cb

                dirtySet
            )
        sw.Restart()
        let mutable count = 0

        let mutable counts = Map.empty

        for d in dirtyResoruces do
            lock d (fun () ->
                if d.OutOfDate then
                    let cnt = match Map.tryFind d.Kind counts with | Some v -> v | None -> 0.0
                    counts <- Map.add d.Kind (cnt + 1.0) counts
                    count <- count + 1

                    d.UpdateCPU()
                    d.UpdateGPU()
            )


        if Config.SyncUploadsAndFrames then OpenTK.Graphics.OpenGL4.GL.Sync()
        sw.Stop()

        //printfn "GL: %fms vs .NET: %fms" glsw.Elapsed.TotalMilliseconds sw.Elapsed.TotalMilliseconds 

        count,counts,sw.Elapsed
