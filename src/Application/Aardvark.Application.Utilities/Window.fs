﻿namespace Aardvark.Application



open System
open Aardvark.Base
open Aardvark.Base.Incremental

open Aardvark.SceneGraph
open Aardvark.Application
open Aardvark.Application.WinForms
open Aardvark.Base.Incremental.Operators
open Aardvark.Base.Rendering
open Aardvark.Base.ShaderReflection
open Aardvark.Rendering.Vulkan
open Aardvark.Rendering.Text
open Aardvark.Application.OpenVR

[<RequireQualifiedAccess>]
type Backend =
    | GL 
    | Vulkan 
    | Both
    
[<RequireQualifiedAccess>]
type Display =
    | Mono
    | Stereo
    | OpenVR

type RenderConfig =
    {
        backend     : Backend
        game        : bool
        debug       : bool
        samples     : int
        display     : Display
        scene       : ISg
        initialCamera  : Option<CameraView>
    }


[<AutoOpen>]
module ``FShade Extensions`` =
    open FShade

    type LightDirAttribute() = inherit FShade.SemanticAttribute("LightDirection")
    type CamDirAttribute() = inherit FShade.SemanticAttribute("CameraDirection")
    type SpecularColorAttribute() = inherit FShade.SemanticAttribute("SpecularColor")

    type UniformScope with
        member x.AmbientColor : V4d = x?Material?AmbientColor
        member x.DiffuseColor : V4d = x?Material?DiffuseColor
        member x.EmissiveColor : V4d = x?Material?EmissiveColor
        member x.ReflectiveColor : V4d = x?Material?ReflectiveColor
        member x.SpecularColor : V4d = x?Material?SpecularColor
        member x.Shininess : float = x?Material?Shininess
        member x.BumpScale : float = x?Material?BumpScale



module Utilities =
    open System.Windows.Forms



    [<AbstractClass>]
    type OutputMod<'a, 'b>(inputs : list<IOutputMod>) =
        inherit AbstractOutputMod<'b>()

        let mutable handle : Option<'a> = None

        abstract member View : 'a -> 'b
        default x.View a = unbox a
        
        abstract member TryUpdate : AdaptiveToken * 'a -> bool
        default x.TryUpdate(_,_) = false

        abstract member Create : AdaptiveToken -> 'a
        abstract member Destroy : 'a -> unit

        override x.Create() =
            for i in inputs do i.Acquire()

        override x.Destroy() =
            for i in inputs do i.Release()
            match handle with
                | Some h -> 
                    x.Destroy h
                    handle <- None
                | _ ->
                    ()

        override x.Compute(t, rt) =
            let handle = 
                match handle with
                    | Some h ->
                        if not (x.TryUpdate(t, h)) then
                            x.Destroy(h)
                            let h = x.Create(t)
                            handle <- Some h
                            h
                        else
                            h
                    | None ->
                        let h = x.Create t
                        handle <- Some h
                        h
            x.View handle
                    
    module OutputMod =
        let custom (dependent : list<IOutputMod>) (create : AdaptiveToken -> 'a) (tryUpdate : AdaptiveToken -> 'a -> bool) (destroy : 'a -> unit) (view : 'a -> 'b) =
            { new OutputMod<'a, 'b>(dependent) with
                override x.Create t = create t
                override x.TryUpdate(t,h) = tryUpdate t h
                override x.Destroy h = destroy h
                override x.View h = view h
            } :> IOutputMod<_> 
            
        let simple (create : AdaptiveToken -> 'a) (destroy : 'a -> unit) =
            { new OutputMod<'a, 'a>([]) with
                override x.Create t = create t
                override x.Destroy h = destroy h
            } :> IOutputMod<_>

    module private Shader =
        open FShade

        let stereoTexture =
            sampler2dArray {
                texture uniform?DiffuseColorTexture
                filter Filter.MinMagLinear
                addressU WrapMode.Wrap
                addressV WrapMode.Wrap
            }

        let renderStereo (v : Effects.Vertex) =
            fragment {
                let index =
                    if v.tc.X > 0.5 then 1
                    else 0

                let tc =
                    if v.tc.X > 0.5 then V2d((v.tc.X - 0.5) * 2.0, v.tc.Y)
                    else V2d(v.tc.X * 2.0, v.tc.Y)

                return stereoTexture.SampleLevel(tc, index, uniform?Dependent)
            }


    type ISimpleRenderWindow =
        inherit IDisposable

        abstract member Runtime : IRuntime
        abstract member Sizes : IMod<V2i>
        abstract member Samples : int
        abstract member Time : IMod<DateTime>
        abstract member Keyboard : IKeyboard
        abstract member Mouse : IMouse

        abstract member View : IMod<Trafo3d[]>
        abstract member Proj : IMod<Trafo3d[]>

        abstract member Scene : ISg with get, set
        abstract member Run : ?preventDisposal:bool -> unit

    [<AbstractClass>]
    type private SimpleRenderWindow(win : IRenderWindow, view : IMod<Trafo3d[]>, proj : IMod<Trafo3d[]>) =
        let mutable scene = Sg.empty


        abstract member Compile : IRenderWindow * ISg -> IRenderTask
        abstract member Release : unit -> unit
        default x.Release() = ()

        member x.Dispose() =
            x.Release()
            win.TryDispose() |> ignore

        member x.Runtime = win.Runtime
        member x.Sizes = win.Sizes
        member x.Samples = win.Samples
        member x.Time = win.Time
        member x.Keyboard = win.Keyboard
        member x.Mouse = win.Mouse

        member x.Run(preventDisposal) = 
            win.Run()
            match preventDisposal with
                | Some t when t = true -> ()
                | _ -> x.Dispose()

        member x.RunWithoutDisposing() = 
            win.Run()

        member x.Scene
            with get() = scene
            and set sg = 
                scene <- sg
                let task = x.Compile(win, sg)
                win.RenderTask <- task
        
        interface ISimpleRenderWindow with
            member x.Runtime = x.Runtime
            member x.Sizes = x.Sizes
            member x.Samples = x.Samples
            member x.Time = x.Time
            member x.Keyboard = x.Keyboard
            member x.Mouse = x.Mouse
            member x.Run(?preventDisposal) = x.Run(preventDisposal)
            
            member x.View = view
            member x.Proj = proj

            member x.Scene
                with get() = x.Scene
                and set sg = x.Scene <- sg

        interface IDisposable with
            member x.Dispose() = x.Dispose()

    let private hookSg (config : RenderConfig )(win : IRenderControl) (sg : ISg) =
        let fillMode = Mod.init FillMode.Fill
        let cullMode = Mod.init CullMode.None

        let status = Mod.init ""

        let alt = win.Keyboard.IsDown(Aardvark.Application.Keys.LeftAlt)
        win.Keyboard.KeyDown(Aardvark.Application.Keys.X).Values.Add (fun () ->
            if Mod.force alt then
                transact (fun () ->
                    let newFillMode = 
                        match fillMode.Value with
                            | FillMode.Fill -> FillMode.Line
                            | _ -> FillMode.Fill

                    status.Value <- sprintf "fill: %A" newFillMode
                    fillMode.Value <- newFillMode
                )
            ()
        )

        win.Keyboard.KeyDown(Aardvark.Application.Keys.Y).Values.Add (fun () ->
            if Mod.force alt then
                transact (fun () ->
                    let newCull = 
                        match cullMode.Value with
                            | CullMode.None -> CullMode.Clockwise
                            | CullMode.Clockwise -> CullMode.CounterClockwise
                            | _ -> CullMode.None
                    status.Value <- sprintf "cull: %A" newCull
                    cullMode.Value <- newCull
                )
            ()
        )

        let teaser = 
            "press 'H' for help"

        let helpText = 
            String.concat "\r\n" [
                "Key Bindings:"
                "  H             toggle this Help"
                "  Alt + X       toggle FillMode"
                "  Alt + Y       toggle CullMode"
                "  WSAD          move camera"
                ""
                "Navigation:"
                "  Left Mouse    look around"
                "  Right Mouse   zoom"
                "  Middle Mouse  pan"
                "  Scroll        zoom"
                ""
            ]

        let overlay =
            match Environment.OSVersion with
                | Windows when config.backend <> Backend.Both ->  
                    let status =
                        adaptive {
                            let! cull = cullMode
                            let! fill = fillMode

                            return 
                                String.concat "\r\n" [
                                    "Status:"
                                    sprintf "  CullMode: %A" cull
                                    sprintf "  FillMode: %A" fill
                                ]
                        }

                    let help = status |> Mod.map (fun s -> helpText + "\r\n" + s)

                    let showHelp = Mod.init false

                    win.Keyboard.KeyDown(Aardvark.Application.Keys.H).Values.Add (fun () ->
                        transact (fun () -> showHelp.Value <- not showHelp.Value)
                    )

                    let text = showHelp |> Mod.bind (function true -> help | false -> Mod.constant teaser)


                    

                    let trafo = 
                        win.Sizes |> Mod.map (fun s -> 
                            let border = V2d(20.0, 10.0) / V2d s
                            let pixels = 30.0 / float s.Y
                            Trafo3d.Scale(pixels) *
                            Trafo3d.Scale(float s.Y / float s.X, 1.0, 1.0) *
                            Trafo3d.Translation(-1.0 + border.X, 1.0 - border.Y - pixels, -1.0)
                        )

                    let font = Font "Consolas"
                    
                    let chars =
                        seq {
                            for c in 0 .. 255 do yield char c
                        }

                    win.Runtime.PrepareGlyphs(font, chars)
                    Sg.text font C4b.White text
                        |> Sg.trafo trafo
                        |> Sg.uniform "ViewTrafo" (Mod.constant Trafo3d.Identity)
                        |> Sg.uniform "ProjTrafo" (Mod.constant Trafo3d.Identity)
                        |> Sg.viewTrafo (Mod.constant Trafo3d.Identity)
                        |> Sg.projTrafo (Mod.constant Trafo3d.Identity)
                | _ ->
                    Sg.empty

        let sg = sg |> Sg.fillMode fillMode |> Sg.cullMode cullMode
        sg, overlay

    let private createMonoScreen (cfg : RenderConfig) =
        let app =
            match cfg.backend with
                | Backend.GL -> new OpenGlApplication(cfg.debug) :> IApplication
                | Backend.Vulkan -> new VulkanApplication(cfg.debug) :> IApplication
                | Backend.Both -> new MultiApplication([|new OpenGlApplication(cfg.debug) :> IApplication; new VulkanApplication(cfg.debug) :> IApplication|]) :> IApplication

        let win = 
            if cfg.game && cfg.backend = Backend.GL then (unbox<OpenGlApplication> app).CreateGameWindow(cfg.samples) :> IRenderWindow
            else app.CreateSimpleRenderWindow(cfg.samples) :> IRenderWindow


        let initialView = 
            match cfg.initialCamera with
                | None -> CameraView.lookAt (V3d(6.0, 6.0, 6.0)) V3d.Zero V3d.OOI
                | Some c -> c

        let view =
            initialView
                |> DefaultCameraController.control win.Mouse win.Keyboard win.Time
                |> Mod.map CameraView.viewTrafo

        let proj =
            win.Sizes 
                |> Mod.map (fun s -> Frustum.perspective 60.0 0.1 1000.0 (float s.X / float s.Y))
                |> Mod.map Frustum.projTrafo

        { new SimpleRenderWindow(win, view |> Mod.map Array.singleton, proj |> Mod.map Array.singleton) with
            override x.Compile(win, sg) =
                let sg, overlay = sg |> hookSg cfg win
                sg
                |> Sg.viewTrafo view
                |> Sg.projTrafo proj
                |> Sg.andAlso overlay
                |> Sg.compile win.Runtime win.FramebufferSignature

            override x.Release() = 
               
                app.Dispose()

        } :> ISimpleRenderWindow

    let private createStereoScreen (cfg : RenderConfig) =
        let app =
            match cfg.backend with
                | Backend.GL -> new OpenGlApplication(cfg.debug) :> IApplication
                | Backend.Vulkan -> new VulkanApplication(cfg.debug) :> IApplication
                | Backend.Both -> failwith "not implemented"

        let win = app.CreateSimpleRenderWindow(cfg.samples)
        let runtime = app.Runtime

        let samples = cfg.samples

        
        let monoSignature =
            runtime.CreateFramebufferSignature(
                SymDict.ofList [
                    DefaultSemantic.Colors, { format = RenderbufferFormat.Rgba8; samples = samples }
                    DefaultSemantic.Depth, { format = RenderbufferFormat.Depth24Stencil8; samples = samples }
                ],
                Set.empty,
                1, 
                Set.empty
            )  

        let signature =
            runtime.CreateFramebufferSignature(
                SymDict.ofList [
                    DefaultSemantic.Colors, { format = RenderbufferFormat.Rgba8; samples = samples }
                    DefaultSemantic.Depth, { format = RenderbufferFormat.Depth24Stencil8; samples = samples }
                ],
                Set.empty,
                2, 
                Set.ofList [
                    "ProjTrafo"; 
                    "ViewTrafo"; 
                    "ModelViewTrafo"; 
                    "ViewProjTrafo"; 
                    "ModelViewProjTrafo"
                    
                    "ProjTrafoInv"; 
                    "ViewTrafoInv"; 
                    "ModelViewTrafoInv"; 
                    "ViewProjTrafoInv"; 
                    "ModelViewProjTrafoInv"
                ]
            )  

        let s = win.Sizes |> Mod.map (fun s -> s / V2i(2,1))

        let colors =
            OutputMod.custom 
                []
                (fun t -> runtime.CreateTextureArray(s.GetValue t, TextureFormat.Rgba8, 1, samples, 2))
                (fun t h -> h.Size.XY = s.GetValue t)
                (fun h -> runtime.DeleteTexture h)
                id
                
        let depth =
            OutputMod.custom 
                []
                (fun t -> runtime.CreateTextureArray(s.GetValue t, TextureFormat.Depth24Stencil8, 1, samples, 2))
                (fun t h -> h.Size.XY = s.GetValue t)
                (fun h -> runtime.DeleteTexture h)
                id

        let resolved =
            OutputMod.custom 
                []
                (fun t -> runtime.CreateTextureArray(s.GetValue t, TextureFormat.Rgba8, 1, 1, 2))
                (fun t h -> h.Size.XY = s.GetValue t)
                (fun h -> runtime.DeleteTexture h)
                id
                
        let framebuffer =
            OutputMod.custom
                [colors; depth]
                (fun t -> runtime.CreateFramebuffer(signature, [DefaultSemantic.Colors, colors.GetValue(t).[TextureAspect.Color, 0] :> IFramebufferOutput; DefaultSemantic.Depth, depth.GetValue(t).[TextureAspect.Depth, 0] :> IFramebufferOutput]))
                (fun t h -> false)
                (fun h -> runtime.DeleteFramebuffer h)
                id

        let initialView = 
            match cfg.initialCamera with
                | None -> CameraView.lookAt (V3d(6.0, 6.0, 6.0)) V3d.Zero V3d.OOI
                | Some c -> c

        let view = 
            initialView
                |> DefaultCameraController.control win.Mouse win.Keyboard win.Time
                |> Mod.map CameraView.viewTrafo

        let near = 0.1
        let far = 1000.0

        let views =
            view |> Mod.map (fun view ->
                [| 
                    view * Trafo3d.Translation(0.05, 0.0, 0.0)
                    view * Trafo3d.Translation(-0.05, 0.0, 0.0)
                |]
            )

        let projs =
            // taken from oculus rift
            let outer = 1.0537801252809621805875367233154
            let inner = 0.77567951104961310377955052031392

            win.Sizes |> Mod.map (fun size ->
                let aspect = float size.X / float size.Y 
                let y = tan (120.0 * Constant.RadiansPerDegree / 2.0) / aspect //(outer + inner) / (2.0 * aspect)

                [|
                    { left = -outer * near; right = inner * near; top = y * near; bottom = -y * near; near = near; far = far } |> Frustum.projTrafo 
                    { left = -inner * near; right = outer * near; top = y * near; bottom = -y * near; near = near; far = far } |> Frustum.projTrafo 
                |]
            )

        let compile (config : RenderConfig) (sg : ISg) =

            framebuffer.Acquire()
            resolved.Acquire()

            let sg, overlay = sg |> hookSg config win

            let stereoTask =
                sg
                |> Sg.uniform "ViewTrafo" views
                |> Sg.uniform "ProjTrafo" projs
                |> Sg.viewTrafo view
                |> Sg.uniform "CameraLocation" (view |> Mod.map (fun t -> t.Backward.C3.XYZ))
                |> Sg.uniform "LightLocation" (view |> Mod.map (fun t -> t.Backward.C3.XYZ))
                |> Sg.compile runtime signature

            let clearTask =
                runtime.CompileClear(signature, ~~C4f.Black, ~~1.0)

            let dependent =
                Mod.custom (fun t ->
                    let fbo = framebuffer.GetValue t
                    let output = OutputDescription.ofFramebuffer fbo

                    let r = resolved.GetValue(t)

                    clearTask.Run(t, RenderToken.Empty, output)
                    stereoTask.Run(t, RenderToken.Empty, output)
                    runtime.Copy(colors.GetValue(t), 0, 0, r, 0, 0, 2, 1)

                    r :> ITexture
                )

            let task =
                Sg.fullScreenQuad
                    |> Sg.uniform "Dependent" (Mod.constant 0.0)
                    |> Sg.diffuseTexture dependent
                    |> Sg.shader {
                        do! Shader.renderStereo
                    }
                    |> Sg.andAlso overlay
                    |> Sg.compile runtime win.FramebufferSignature

            let dummy =
                { new AbstractRenderTask() with
                    member x.FramebufferSignature = Some win.FramebufferSignature
                    member x.Runtime = Some win.Runtime
                    member x.PerformUpdate (_,_) = ()
                    member x.Perform (_,_,_) = ()
                    member x.Release() =
                        stereoTask.Dispose()
                        clearTask.Dispose()
                        framebuffer.Release()
                        resolved.Release()
                    member x.Use f = f()
                }

            RenderTask.ofList [dummy; task]


        let res =
            { new SimpleRenderWindow(win, views, projs) with
                override x.Compile(win, sg) =
                    compile cfg sg

                override x.Release() =
                    win.Dispose()
                    runtime.DeleteFramebufferSignature signature
        
                    app.Dispose()
        

            } :> ISimpleRenderWindow

        res

    let private createOpenVR (cfg : RenderConfig) =
        match cfg.backend with
            | Backend.Vulkan ->
                let app = VulkanVRApplicationLayered(cfg.samples, cfg.debug)

                let hmdLocation = app.Hmd.MotionState.Pose |> Mod.map (fun t -> t.Forward.C3.XYZ)


                { new SimpleRenderWindow(app, app.Info.viewTrafos, app.Info.projTrafos) with
                    override x.Compile(win, sg) =
                        sg
                        |> Sg.uniform "ViewTrafo" app.Info.viewTrafos
                        |> Sg.uniform "ProjTrafo" app.Info.projTrafos
                        |> Sg.uniform "CameraLocation" hmdLocation
                        |> Sg.uniform "LightLocation" hmdLocation
                        |> Sg.compile app.Runtime app.FramebufferSignature
                } :> ISimpleRenderWindow

            | Backend.GL -> 
                failwith "no OpenGL OpenVR backend atm."

            | Backend.Both -> 
                failwith "no OpenGL OpenVR backend atm."


    let createWindow (cfg : RenderConfig) =
        match cfg.display with
            | Display.Mono -> createMonoScreen cfg
            | Display.Stereo -> createStereoScreen cfg
            | Display.OpenVR -> createOpenVR cfg

    let runConfig (cfg : RenderConfig) =
        let win = createWindow cfg
        win.Scene <- cfg.scene
        win.Run()

    let run (display : Display) (backend : Backend) (scene : ISg) =
        runConfig {
            scene = scene
            game = false
            display = display
            backend = backend
            debug = true
            samples = 8
            initialCamera = None
        }

[<AutoOpen>]
module ``Render Utilities`` =
    type ShowBuilder() =
        member x.Yield(()) =
            {
                backend = Backend.Vulkan
                debug = true
                game = false
                samples = 8
                display = Display.Mono
                scene = Sg.empty
                initialCamera = None
            }

        [<CustomOperation("backend")>]
        member x.Backend(s : RenderConfig, b : Backend) =
            { s with backend = b }

        [<CustomOperation("debug")>]
        member x.Debug(s : RenderConfig, d : bool) =
            { s with debug = d }

        [<CustomOperation("samples")>]
        member x.Samples(state : RenderConfig, s : int) =
            { state with samples = s }

        [<CustomOperation("display")>]
        member x.Display(s : RenderConfig, d : Display) =
            { s with display = d }

        [<CustomOperation("scene")>]
        member x.Scene(state : RenderConfig, s : ISg) =
            { state with scene = s }

        [<CustomOperation("initialCamera")>]
        member x.InitialCamera(state : RenderConfig, c : CameraView) =
            { state with initialCamera = Some c }

        [<CustomOperation("game")>]
        member x.Game(state : RenderConfig, game : bool) =
            { state with game = game }

        member x.Run(cfg : RenderConfig) =
            Utilities.runConfig cfg

    type WindowBuilder() =
        member x.Yield(()) =
            {
                backend = Backend.Vulkan
                debug = true
                game = false
                samples = 8
                display = Display.Mono
                scene = Sg.empty
                initialCamera = None
            }

        [<CustomOperation("backend")>]
        member x.Backend(s : RenderConfig, b : Backend) =
            { s with backend = b }

        [<CustomOperation("debug")>]
        member x.Debug(s : RenderConfig, d : bool) =
            { s with debug = d }

        [<CustomOperation("samples")>]
        member x.Samples(state : RenderConfig, s : int) =
            { state with samples = s }

        [<CustomOperation("display")>]
        member x.Display(s : RenderConfig, d : Display) =
            { s with display = d }

        [<CustomOperation("game")>]
        member x.Game(state : RenderConfig, game : bool) =
            { state with game = game }

        [<CustomOperation("initialCamera")>]
        member x.InitialCamera(state : RenderConfig, c : CameraView) =
            { state with initialCamera = Some c }

        member x.Run(cfg : RenderConfig) =
            Utilities.createWindow cfg
            
    let window = WindowBuilder()
    let show = ShowBuilder()