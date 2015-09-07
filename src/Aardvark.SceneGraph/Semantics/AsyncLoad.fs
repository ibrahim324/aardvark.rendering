﻿namespace Aardvark.SceneGraph.Semantics

open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.Base.Ag
open Aardvark.Base.AgHelpers
open Aardvark.SceneGraph
open Aardvark.Base.Rendering



[<Semantic>]
type AsyncLoadSemantics() =
    let r = System.Random()

    member x.RenderObjects(app : Sg.AsyncLoadApplicator) : aset<IRenderObject> =
        aset {
            let! child = app.Child
            let runtime = app.Runtime
            for ro in child.RenderObjects() do
                let! prep = 
                    Mod.async (
                        async { 
                            printfn "starting"
                            do! Async.Sleep (r.Next(5000, 50000))
                            printfn "done!!!!"
                            return runtime.PrepareRenderObject ro 
                        }
                    )
                match prep with
                    | None -> ()
                    | Some prep -> yield prep
        }