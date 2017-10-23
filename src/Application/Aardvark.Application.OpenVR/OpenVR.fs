﻿namespace Aardvark.Application.OpenVR

open System
open Valve.VR
open Aardvark.Base
open Aardvark.Base.Incremental
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

#nowarn "9"

type VrDeviceType =
    | Other = 0
    | Hmd = 1
    | Controller = 2
    | TrackingReference = 3

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module VrDeviceType =
    let internal ofETrackedDeviceClass =
        LookupTable.lookupTable [
            ETrackedDeviceClass.Controller, VrDeviceType.Controller
            ETrackedDeviceClass.HMD, VrDeviceType.Hmd
            ETrackedDeviceClass.TrackingReference, VrDeviceType.TrackingReference

            ETrackedDeviceClass.DisplayRedirect, VrDeviceType.Other
            ETrackedDeviceClass.GenericTracker, VrDeviceType.Other
            ETrackedDeviceClass.Invalid, VrDeviceType.Other
        ]

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Trafo =
    let private flip = Trafo3d.FromBasis(V3d.IOO, V3d.OOI, -V3d.OIO, V3d.Zero)

    let internal ofHmdMatrix34 (x : HmdMatrix34_t) =
        let t = 
            M44f(
                x.m0, x.m4, x.m8,  0.0f,
                x.m1, x.m5, x.m9,  0.0f,
                x.m2, x.m6, x.m10, 0.0f,
                x.m3, x.m7, x.m11, 1.0f
            ) 

        let t = M44d.op_Explicit(t)
        Trafo3d(t,t.Inverse) * flip

    let internal ofHmdMatrix44 (x : HmdMatrix44_t) =
        let t = M44f(x.m0,x.m1,x.m2,x.m3,x.m4,x.m5,x.m6,x.m7,x.m8,x.m9,x.m10,x.m11,x.m12,x.m13,x.m14,x.m15) 
        let t = M44d.op_Explicit(t)
        Trafo3d(t,t.Inverse)


    let internal angularVelocity (v : HmdVector3_t) =
        let v = V3d(-v.v0, -v.v1, -v.v2) // transposed world
        flip.Forward.TransformDir v


    let internal velocity (v : HmdVector3_t) =
        let v = V3d(v.v0, v.v1, v.v2)
        flip.Forward.TransformDir v

    let inline inverse (t : Trafo3d) = t.Inverse

type MotionState() =
    let isValid = Mod.init false
    let pose = Mod.init Trafo3d.Identity
    let angularVelocity = Mod.init V3d.Zero
    let velocity = Mod.init V3d.Zero

    member x.IsValid = isValid :> IMod<_>
    member x.Pose = pose :> IMod<_>
    member x.Velocity = velocity :> IMod<_>
    member x.AngularVelocity = angularVelocity :> IMod<_>

    member internal x.Update(newPose : byref<TrackedDevicePose_t>) =
        if newPose.bDeviceIsConnected && newPose.bPoseIsValid && newPose.eTrackingResult = ETrackingResult.Running_OK then
            let t = Trafo.ofHmdMatrix34 newPose.mDeviceToAbsoluteTracking
            isValid.Value <- true
            pose.Value <- t
            angularVelocity.Value <- Trafo.angularVelocity newPose.vAngularVelocity
            velocity.Value <- Trafo.velocity newPose.vVelocity
        else
            isValid.Value <- false

type VrDevice(system : CVRSystem, deviceType : VrDeviceType, index : int) =
    
    let getString (prop : ETrackedDeviceProperty) =
        let builder = System.Text.StringBuilder(4096, 4096)
        let mutable err = ETrackedPropertyError.TrackedProp_Success
        let len = system.GetStringTrackedDeviceProperty(uint32 index, prop, builder, uint32 builder.Capacity, &err)
        builder.ToString()

    let getInt (prop : ETrackedDeviceProperty) =
        let mutable err = ETrackedPropertyError.TrackedProp_Success
        let len = system.GetInt32TrackedDeviceProperty(uint32 index, prop, &err)

        len

    let vendor  = lazy ( getString ETrackedDeviceProperty.Prop_ManufacturerName_String )
    let model   = lazy ( getString ETrackedDeviceProperty.Prop_ModelNumber_String )
    
    let state = MotionState()

    member x.Type = deviceType

    member x.MotionState = state

    member internal x.Update(poses : TrackedDevicePose_t[]) =
        state.Update(&poses.[index])

type VrTexture =
    class
        val mutable public Data : nativeint
        val mutable public Info : Texture_t
        val mutable public Flags : EVRSubmitFlags
        val mutable public Bounds : VRTextureBounds_t

        new(d,i,f,b) = { Data = d; Info = i; Flags = f; Bounds = b }

        static member OpenGL(handle : int) =
            let i = Texture_t(eColorSpace = EColorSpace.Auto, eType = ETextureType.OpenGL, handle = nativeint handle)
            let b = VRTextureBounds_t(uMin = 0.0f, uMax = 1.0f, vMin = 0.0f, vMax = 1.0f)
            new VrTexture(0n, i, EVRSubmitFlags.Submit_Default, b)

        static member Vulkan(data : VRVulkanTextureData_t) =
            let ptr = Marshal.AllocHGlobal sizeof<VRVulkanTextureData_t>
            NativeInt.write ptr data
            let i = Texture_t(eColorSpace = EColorSpace.Auto, eType = ETextureType.Vulkan, handle = ptr)
            let b = VRTextureBounds_t(uMin = 0.0f, uMax = 1.0f, vMin = 0.0f, vMax = 1.0f)
            new VrTexture(ptr, i, EVRSubmitFlags.Submit_Default, b)
            
        static member D3D12(data : D3D12TextureData_t) =
            let ptr = Marshal.AllocHGlobal sizeof<D3D12TextureData_t>
            NativeInt.write ptr data
            let i = Texture_t(eColorSpace = EColorSpace.Auto, eType = ETextureType.DirectX12, handle = ptr)
            let b = VRTextureBounds_t(uMin = 0.0f, uMax = 1.0f, vMin = 0.0f, vMax = 1.0f)
            new VrTexture(ptr, i, EVRSubmitFlags.Submit_Default, b)
            
        member x.Dispose() =
            if x.Data <> 0n then Marshal.FreeHGlobal x.Data

        interface IDisposable with
            member x.Dispose() = x.Dispose()

    end

type VrRenderInfo =
    {
        framebufferSize     : V2i
        viewTrafo           : IMod<Trafo3d>
        lProjTrafo          : IMod<Trafo3d>
        rProjTrafo          : IMod<Trafo3d>
    }

[<AbstractClass>]
type VrRenderer() =
    let system =
        let mutable err = EVRInitError.None
        let sys = OpenVR.Init(&err)
        if err <> EVRInitError.None then
            Log.error "[OpenVR] %s" (OpenVR.GetStringForHmdError err)
            failwithf "[OpenVR] %s" (OpenVR.GetStringForHmdError err)
        sys
    
    let devices =
        [|
            for i in 0u .. OpenVR.k_unMaxTrackedDeviceCount-1u do
                let deviceType = system.GetTrackedDeviceClass i
                if deviceType <> ETrackedDeviceClass.Invalid then
                    yield VrDevice(system, VrDeviceType.ofETrackedDeviceClass deviceType, int i)
        |]

    let hmds = devices |> Array.filter (fun d -> d.Type = VrDeviceType.Hmd)
    let controllers = devices |> Array.filter (fun d -> d.Type = VrDeviceType.Controller)
    
    let compositor = OpenVR.Compositor
    let renderPoses = Array.zeroCreate (int OpenVR.k_unMaxTrackedDeviceCount)
    let gamePoses = Array.zeroCreate (int OpenVR.k_unMaxTrackedDeviceCount)

    [<VolatileField>]
    let mutable isAlive = true

    let check (str : string) (err : EVRCompositorError) =
        if err <> EVRCompositorError.None then
            Log.error "[OpenVR] %A: %s" err str
            failwithf "[OpenVR] %A: %s" err str

    let depthRange = Range1d(0.1, 100.0) |> Mod.init

    let lProj =
        let headToEye = system.GetEyeToHeadTransform(EVREye.Eye_Left) |> Trafo.ofHmdMatrix34 |> Trafo.inverse
        depthRange |> Mod.map (fun range ->
            let proj = system.GetProjectionMatrix(EVREye.Eye_Left, float32 range.Min, float32 range.Max)
            headToEye * Trafo.ofHmdMatrix44 proj
        )

    let rProj =
        let headToEye = system.GetEyeToHeadTransform(EVREye.Eye_Right) |> Trafo.ofHmdMatrix34 |> Trafo.inverse
        depthRange |> Mod.map (fun range ->
            let proj = system.GetProjectionMatrix(EVREye.Eye_Left, float32 range.Min, float32 range.Max)
            headToEye * Trafo.ofHmdMatrix44 proj
        )

    let desiredSize =
        let mutable width = 0u
        let mutable height = 0u
        system.GetRecommendedRenderTargetSize(&width,&height)
        V2i(int width, int height)


    let infos =
        hmds |> Array.map (fun hmd ->
            {
                framebufferSize = desiredSize
                viewTrafo = hmd.MotionState.Pose |> Mod.map Trafo.inverse
                lProjTrafo = lProj
                rProjTrafo = rProj
            }
        )

    member x.DesiredSize = desiredSize

    member x.Shutdown() =
        isAlive <- false

    abstract member OnLoad : info : VrRenderInfo -> VrTexture * VrTexture
    abstract member Render : unit -> unit
    abstract member Release : unit -> unit

    member x.Run (render : VrRenderInfo * VrRenderInfo -> VrTexture * VrTexture) =
        if not isAlive then raise <| ObjectDisposedException("VrSystem")
        let (lTex, rTex) = x.OnLoad infos.[0] 

        while isAlive do
            let err = compositor.WaitGetPoses(renderPoses, gamePoses)
            if err = EVRCompositorError.None then

                // update all poses
                transact (fun () ->
                    for d in devices do d.Update(renderPoses)
                )
            
                // render for all HMDs
                for i in 0 .. hmds.Length - 1 do
                    let hmd = hmds.[i]
                     
                    if hmd.MotionState.IsValid.GetValue() then
                        x.Render()

                        compositor.Submit(EVREye.Eye_Left, &lTex.Info, &lTex.Bounds, lTex.Flags) |> check "submit left"
                        compositor.Submit(EVREye.Eye_Right, &rTex.Info, &rTex.Bounds, rTex.Flags) |> check "submit right"

            else
                Log.error "[OpenVR] %A" err
        
        lTex.Dispose()
        rTex.Dispose()
        x.Release()
        OpenVR.Shutdown()

    member x.Hmd = hmds.[0]


    member x.Controllers = controllers



module Test =

    let run () = 
        let mutable err = EVRInitError.None
        let sys = OpenVR.Init(&err)
        if err <> EVRInitError.None then
            Log.error "[OpenVR] %s" (OpenVR.GetStringForHmdError err)
            failwithf "[OpenVR] %s" (OpenVR.GetStringForHmdError err)

        let compositor = OpenVR.Compositor

        let t : Texture_t = failwith ""

        //compositor.Submit()


        ()

