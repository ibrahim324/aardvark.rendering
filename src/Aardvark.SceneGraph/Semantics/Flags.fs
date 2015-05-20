﻿namespace Aardvark.SceneGraph.Semantics

open Aardvark.Base
open Aardvark.Base.Incremental
open Aardvark.Base.Ag
open Aardvark.Base.AgHelpers
open Aardvark.SceneGraph

open Aardvark.SceneGraph.Internal

module ActiveSemantics =

    [<Semantic>]
    type ActiveSemantics() =

        let trueConstant = Mod.initConstant true
        let andCache = Caching.BinaryOpCache (Mod.map2 (&&))

        let (<&>) (a : IMod<bool>) (b : IMod<bool>) =
            if a = trueConstant then b
            elif b = trueConstant then a
            else andCache.Invoke a b

        member x.IsActive(r : Root) =
            r.Child?IsActive <- trueConstant

        member x.IsActive(o : Sg.OnOffNode) =
            o.Child?IsActive <- o?IsActive <&> o.IsActive


    [<Semantic>]
    type PassSemantics() =

        let defaultPass = Mod.initConstant 0UL
        
        member x.RenderPass(e : Root) =
            e.Child?RenderPass <- defaultPass

        member x.RenderPass(p : Sg.PassApplicator) =
            p.Child?RenderPass <- p.Pass