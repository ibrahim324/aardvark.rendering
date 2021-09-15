﻿namespace Aardvark.Rendering.GL

open System
open System.Threading
open System.Runtime.InteropServices
open Aardvark.Base
open Aardvark.Base.NativeTensors
open Aardvark.Rendering
open OpenTK.Graphics.OpenGL4
open Microsoft.FSharp.NativeInterop
open System.Runtime.CompilerServices
open Aardvark.Rendering.GL

#nowarn "9"

type Texture =
    class
        val mutable public Context : Context
        val mutable public Handle : int
        val mutable public Dimension : TextureDimension
        val mutable public Multisamples : int
        val mutable public Size : V3i
        val mutable public Count : int
        val mutable public Format : TextureFormat
        val mutable public MipMapLevels : int
        val mutable public SizeInBytes : int64
        val mutable public ImmutableFormat : bool
        val mutable public IsArray : bool

        member x.IsMultisampled = x.Multisamples > 1

        member x.Size1D = x.Size.X
        member x.Size2D = x.Size.XY
        member x.Size3D = x.Size
        
        interface IBackendTexture with
            member x.Runtime = x.Context.Runtime :> ITextureRuntime
            member x.WantMipMaps = x.MipMapLevels > 1
            member x.Dimension = x.Dimension
            member x.MipMapLevels = x.MipMapLevels
            member x.Handle = x.Handle :> obj
            member x.Size = x.Size
            member x.Count = x.Count
            member x.Format = x.Format
            member x.Samples = x.Multisamples

        new(ctx : Context, handle : int, dimension : TextureDimension, mipMapLevels : int, multisamples : int, size : V3i, count : Option<int>, format : TextureFormat, sizeInBytes : int64, immutable : bool) =
            let cnt, isArray =
                match count with
                    | Some cnt -> cnt, true
                    | None -> 1, false
            { Context = ctx; Handle = handle; Dimension = dimension; MipMapLevels = mipMapLevels; Multisamples = multisamples; Size = size; Count = cnt; IsArray = isArray; Format = format; SizeInBytes = sizeInBytes; ImmutableFormat = immutable }

    end

type TextureViewHandle(ctx : Context, handle : int, dimension : TextureDimension, mipMapLevels : int, multisamples : int, size : V3i, count : Option<int>, format : TextureFormat) = 
    inherit Texture(ctx, handle, dimension, mipMapLevels, multisamples, size, count, format, 0L, true)
        

[<AutoOpen>]
module TextureCubeExtensions =
    // PositiveX = 0,
    // NegativeX = 1,
    // PositiveY = 2,
    // NegativeY = 3,
    // PositiveZ = 4,
    // NegativeZ = 5,
    // cubeSides are sorted like in their implementation (making some things easier)
    let cubeSides =
        [|
            CubeSide.PositiveX, TextureTarget.TextureCubeMapPositiveX
            CubeSide.NegativeX, TextureTarget.TextureCubeMapNegativeX

            CubeSide.PositiveY, TextureTarget.TextureCubeMapPositiveY
            CubeSide.NegativeY, TextureTarget.TextureCubeMapNegativeY
                
            CubeSide.PositiveZ, TextureTarget.TextureCubeMapPositiveZ
            CubeSide.NegativeZ, TextureTarget.TextureCubeMapNegativeZ
        |]


[<AutoOpen>]
module ResourceCounts =

    let addTexture (ctx:Context) size =
        Interlocked.Increment(&ctx.MemoryUsage.TextureCount) |> ignore
        Interlocked.Add(&ctx.MemoryUsage.TextureMemory,size) |> ignore

    let addTextureView (ctx:Context) =
        Interlocked.Increment(&ctx.MemoryUsage.TextureViewCount) |> ignore

    let removeTexture (ctx:Context) size =
        Interlocked.Decrement(&ctx.MemoryUsage.TextureCount)  |> ignore
        Interlocked.Add(&ctx.MemoryUsage.TextureMemory,-size) |> ignore

    let removeTextureView (ctx:Context) =
        Interlocked.Decrement(&ctx.MemoryUsage.TextureViewCount) |> ignore

    let updateTexture (ctx:Context) oldSize newSize =
        Interlocked.Add(&ctx.MemoryUsage.TextureMemory,newSize-oldSize) |> ignore

    let texSizeInBytes (size : V3i, t : TextureFormat, samples : int, levels : int) =
        let pixelCount = (int64 size.X) * (int64 size.Y) * (int64 size.Z) * (int64 samples)
        let mutable size = pixelCount * (int64 (InternalFormat.getSizeInBits (unbox (int t)))) / 8L
        let mutable temp = size
        for i in 1..levels-1 do
            temp <- temp >>> 2
            size <- size + temp
        size

module TextureTarget =
    let ofParameters (dim : TextureDimension) (isArray : bool) (isMS : bool) =
        match dim, isArray, isMS with

            | TextureDimension.Texture1D,      _,       true     -> failwith "Texture1D cannot be multisampled"
            | TextureDimension.Texture1D,      true,    _        -> TextureTarget.Texture1DArray
            | TextureDimension.Texture1D,      false,   _        -> TextureTarget.Texture1D

            | TextureDimension.Texture2D,      false,   false    -> TextureTarget.Texture2D
            | TextureDimension.Texture2D,      true,    false    -> TextureTarget.Texture2DArray
            | TextureDimension.Texture2D,      false,   true     -> TextureTarget.Texture2DMultisample
            | TextureDimension.Texture2D,      true,    true     -> TextureTarget.Texture2DMultisampleArray

            | TextureDimension.Texture3D,      false,   false    -> TextureTarget.Texture3D
            | TextureDimension.Texture3D,      _,       _        -> failwith "Texture3D cannot be multisampled or an array"

            | TextureDimension.TextureCube,   false,    false    -> TextureTarget.TextureCubeMap
            | TextureDimension.TextureCube,   true,     false    -> TextureTarget.TextureCubeMapArray
            | TextureDimension.TextureCube,   _,        true     -> failwith "TextureCube cannot be multisampled"

            | _ -> failwithf "unknown texture dimension: %A" dim

    let ofTexture (texture : Texture) =
        ofParameters texture.Dimension texture.IsArray texture.IsMultisampled


[<AutoOpen>]
module TextureCreationExtensions =
    type Context with
        member private x.SetDefaultTextureParams(target : TextureTarget, mipMapLevels : int) =
            GL.TexParameter(target, TextureParameterName.TextureMaxLevel, mipMapLevels - 1)
            GL.TexParameter(target, TextureParameterName.TextureBaseLevel, 0)
            GL.TexParameter(target, TextureParameterName.TextureWrapS, int TextureWrapMode.ClampToEdge)
            GL.TexParameter(target, TextureParameterName.TextureWrapT, int TextureWrapMode.ClampToEdge)
            GL.TexParameter(target, TextureParameterName.TextureMinFilter, int TextureMinFilter.Linear)
            GL.TexParameter(target, TextureParameterName.TextureMagFilter, int TextureMagFilter.Linear)

        member x.CreateTexture(size : V3i, dim : TextureDimension, format : TextureFormat, slices : int, levels : int, samples : int) =
            let isArray = slices > 0

            match dim, isArray with
            | TextureDimension.Texture1D, false -> x.CreateTexture1D(size.X, levels, format)
            | TextureDimension.Texture1D, true  -> x.CreateTexture1DArray(size.X, slices, levels, format)
            | TextureDimension.Texture2D, false -> x.CreateTexture2D(size.XY, levels, format, samples)
            | TextureDimension.Texture2D, true  -> x.CreateTexture2DArray(size.XY, slices, levels, format, samples)
            | TextureDimension.Texture3D, false -> x.CreateTexture3D(size, levels, format)
            | TextureDimension.Texture3D, true  -> raise <| ArgumentException("3D textures cannot be arrayed")
            | TextureDimension.TextureCube, false -> x.CreateTextureCube(size.X, levels, format, samples)
            | TextureDimension.TextureCube, true  -> x.CreateTextureCubeArray(size.X, slices, levels, format, samples)
            | _ -> failwith "[GL] Invalid texture dimension"

        member x.UpdateTexture(tex : Texture, size : V3i, dim : TextureDimension, format : TextureFormat, slices : int, levels : int, samples : int) =
            let isArray = slices > 0

            match dim, isArray with
            | TextureDimension.Texture1D, false -> x.UpdateTexture1D(tex, size.X, levels, format)
            | TextureDimension.Texture1D, true  -> x.UpdateTexture1DArray(tex, size.X, slices, levels, format)
            | TextureDimension.Texture2D, false -> x.UpdateTexture2D(tex, size.XY, levels, format, samples)
            | TextureDimension.Texture2D, true  -> x.UpdateTexture2DArray(tex, size.XY, slices, levels, format, samples)
            | TextureDimension.Texture3D, false -> x.UpdateTexture3D(tex, size, levels, format)
            | TextureDimension.Texture3D, true  -> raise <| ArgumentException("3D textures cannot be arrayed")
            | TextureDimension.TextureCube, false -> x.UpdateTextureCube(tex, size.X, levels, format, samples)
            | TextureDimension.TextureCube, true  -> x.UpdateTextureCubeArray(tex, size.X, slices, levels, format, samples)
            | _ -> failwith "[GL] Invalid texture dimension"


        member x.CreateTexture1D(size : int, mipMapLevels : int, t : TextureFormat) =
            using x.ResourceLock (fun _ ->
                let h = GL.GenTexture()
                GL.Check "could not create texture"

                addTexture x 0L
                let tex = Texture(x, h, TextureDimension.Texture1D, mipMapLevels, 1, V3i.Zero, None, t, 0L, false)
                x.UpdateTexture1D(tex, size, mipMapLevels, t)

                tex
            )

        member x.CreateTexture2D(size : V2i, mipMapLevels : int, t : TextureFormat, samples : int) =
            using x.ResourceLock (fun _ ->
                let h = GL.GenTexture()
                GL.Check "could not create texture"
                
                addTexture x 0L
                let tex = Texture(x, h, TextureDimension.Texture2D, mipMapLevels, 1, V3i.Zero, None, t, 0L, false)

                x.UpdateTexture2D(tex, size, mipMapLevels, t, samples)

                tex
            )

        member x.CreateTexture3D(size : V3i, mipMapLevels : int, t : TextureFormat) =
            using x.ResourceLock (fun _ ->
                let h = GL.GenTexture()
                GL.Check "could not create texture"

                addTexture x 0L
                let tex = Texture(x, h, TextureDimension.Texture3D, mipMapLevels, 1, V3i.Zero, None, t, 0L, false)
                x.UpdateTexture3D(tex, size, mipMapLevels, t)

                tex
            )

        member x.CreateTextureCube(size : int, mipMapLevels : int, t : TextureFormat, samples : int) =
            using x.ResourceLock (fun _ ->
                let h = GL.GenTexture()
                GL.Check "could not create texture"

                addTexture x 0L
                let tex = Texture(x, h, TextureDimension.TextureCube, mipMapLevels, 1, V3i(size, size, 0), None, t, 0L, false)
                x.UpdateTextureCube(tex, size, mipMapLevels, t, samples)

                tex
            )

        member x.CreateTexture1DArray(size : int, count : int, mipMapLevels : int, t : TextureFormat) =
            using x.ResourceLock (fun _ ->
                let h = GL.GenTexture()
                GL.Check "could not create texture"

                addTexture x 0L
                let tex = Texture(x, h, TextureDimension.Texture1D, mipMapLevels, 1, V3i.Zero, Some count, t, 0L, false)
                x.UpdateTexture1DArray(tex, size, count, mipMapLevels, t)

                tex
            ) 

        member x.CreateTexture2DArray(size : V2i, count : int, mipMapLevels : int, t : TextureFormat, samples : int) =
            using x.ResourceLock (fun _ ->
                let h = GL.GenTexture()
                GL.Check "could not create texture"
                
                addTexture x 0L
                let tex = Texture(x, h, TextureDimension.Texture2D, mipMapLevels, 1, V3i.Zero, Some count, t, 0L, false)

                x.UpdateTexture2DArray(tex, size, count, mipMapLevels, t, samples)

                tex
            )

        member x.CreateTextureCubeArray(size : int, count : int, mipMapLevels : int, t : TextureFormat, samples : int) =
            using x.ResourceLock (fun _ ->
                let h = GL.GenTexture()
                GL.Check "could not create texture"

                addTexture x 0L
                let tex = Texture(x, h, TextureDimension.TextureCube, mipMapLevels, 1, V3i(size, size, 0), Some count, t, 0L, false)
                x.UpdateTextureCubeArray(tex, size, count, mipMapLevels, t, samples)

                tex
            )
            
        member x.UpdateTexture1D(tex : Texture, size : int, mipMapLevels : int, t : TextureFormat) =
            using x.ResourceLock (fun _ ->
                if tex.ImmutableFormat then
                    failwith "cannot update format/size for immutable texture"

                GL.BindTexture(TextureTarget.Texture1D, tex.Handle)
                GL.Check "could not bind texture"

                let sizeInBytes = texSizeInBytes(V3i(size, 1, 1), t, 1, mipMapLevels)
                updateTexture tex.Context tex.SizeInBytes sizeInBytes
                tex.SizeInBytes <- sizeInBytes

                x.SetDefaultTextureParams(TextureTarget.Texture1D, mipMapLevels)

                GL.TexStorage1D(TextureTarget1d.Texture1D, mipMapLevels, unbox (int t), size)
                GL.Check "could not allocate texture"

                GL.BindTexture(TextureTarget.Texture1D, 0)
                GL.Check "could not unbind texture"

                tex.MipMapLevels <- mipMapLevels
                tex.Dimension <- TextureDimension.Texture1D
                tex.Size <- V3i(size, 1, 1)
                tex.Format <- t
                tex.ImmutableFormat <- true
            )

        member x.UpdateTexture2D(tex : Texture, size : V2i, mipMapLevels : int, t : TextureFormat, samples : int) =
            using x.ResourceLock (fun _ ->
                if tex.ImmutableFormat then
                    failwith "cannot update format/size for immutable texture"

                let target =
                    if samples = 1 then TextureTarget.Texture2D
                    else TextureTarget.Texture2DMultisample

                GL.BindTexture(target, tex.Handle)
                GL.Check "could not bind texture"

                let sizeInBytes = texSizeInBytes(size.XYI, t, samples, mipMapLevels)
                updateTexture tex.Context tex.SizeInBytes sizeInBytes
                tex.SizeInBytes <- sizeInBytes

                if samples = 1 then
                    // parameters only valid for non-multisampled textures
                    x.SetDefaultTextureParams(target, mipMapLevels)
                    GL.TexStorage2D(TextureTarget2d.Texture2D, mipMapLevels, unbox (int t), size.X, size.Y)
                else
                    if mipMapLevels > 1 then failwith "multisampled textures cannot have MipMaps"
                    GL.TexStorage2DMultisample(TextureTargetMultisample2d.Texture2DMultisample, samples, unbox (int t), size.X, size.Y, true)

                GL.Check "could not allocate texture"
                GL.BindTexture(target, 0)
                GL.Check "could not unbind texture"

                tex.MipMapLevels <- mipMapLevels
                tex.Dimension <- TextureDimension.Texture2D
                tex.Multisamples <- samples
                tex.Count <- 1
                tex.Size <- V3i(size.X, size.Y, 1)
                tex.Format <- t
                tex.ImmutableFormat <- true
            )

        member x.UpdateTexture3D(tex : Texture, size : V3i, mipMapLevels : int, t : TextureFormat) =
            using x.ResourceLock (fun _ ->
                if tex.ImmutableFormat then
                    failwith "cannot update format/size for immutable texture"

                GL.BindTexture(TextureTarget.Texture3D, tex.Handle)
                GL.Check "could not bind texture"

                let ifmt = unbox (int t) 

                let sizeInBytes = texSizeInBytes(size, t, 1, mipMapLevels)
                updateTexture tex.Context tex.SizeInBytes sizeInBytes
                tex.SizeInBytes <- sizeInBytes

                x.SetDefaultTextureParams(TextureTarget.Texture3D, mipMapLevels)

                GL.TexStorage3D(TextureTarget3d.Texture3D, mipMapLevels, ifmt, size.X, size.Y, size.Z)
                GL.Check "could not allocate texture"

                GL.BindTexture(TextureTarget.Texture3D, 0)
                GL.Check "could not unbind texture"

                tex.MipMapLevels <- mipMapLevels
                tex.Dimension <- TextureDimension.Texture3D
                tex.Count <- 1
                tex.Multisamples <- 1
                tex.Size <- size
                tex.Format <- t
                tex.ImmutableFormat <- true
            )

        member x.UpdateTextureCube(tex : Texture, size : int, mipMapLevels : int, t : TextureFormat, samples : int) =
            using x.ResourceLock (fun _ ->
                if tex.ImmutableFormat then
                    failwith "cannot update format/size for immutable texture"

                GL.BindTexture(TextureTarget.TextureCubeMap, tex.Handle)
                GL.Check "could not bind texture"

                if samples = 1 then
                    x.SetDefaultTextureParams(TextureTarget.TextureCubeMap, mipMapLevels)
                    GL.TexStorage2D(TextureTarget2d.TextureCubeMap, mipMapLevels, unbox (int t), size, size)
                else
                    if mipMapLevels > 1 then failwith "multisampled textures cannot have MipMaps"
                    Log.warn "[GL] cubemap MS not working atm."
                    // TODO: verify that this works!!
                    for f in 0..5 do
                        let target = int TextureTarget.TextureCubeMapPositiveX + f
                        GL.TexImage2DMultisample(unbox target, samples, unbox (int t), size, size, true)

                GL.BindTexture(TextureTarget.TextureCubeMap, 0)
                GL.Check "could not unbind texture"

                let sizeInBytes = texSizeInBytes(V3i(size, size, 1), t, samples, mipMapLevels) * 6L
                updateTexture tex.Context tex.SizeInBytes sizeInBytes
                tex.SizeInBytes <- sizeInBytes

                tex.MipMapLevels <- mipMapLevels
                tex.Dimension <- TextureDimension.TextureCube
                tex.Size <- V3i(size, size, 1)
                tex.Count <- 1
                tex.Multisamples <- samples
                tex.Format <- t
                tex.ImmutableFormat <- true
            )

        member x.UpdateTexture1DArray(tex : Texture, size : int, count : int, mipMapLevels : int, t : TextureFormat) =
            using x.ResourceLock (fun _ ->
                if tex.ImmutableFormat then
                    failwith "cannot update format/size for immutable texture"

                GL.BindTexture(TextureTarget.Texture1DArray, tex.Handle)
                GL.Check "could not bind texture"

                let sizeInBytes = texSizeInBytes(V3i(size, 1, 1), t, 1, mipMapLevels) * (int64 count)
                updateTexture tex.Context tex.SizeInBytes sizeInBytes
                tex.SizeInBytes <- sizeInBytes

                x.SetDefaultTextureParams(TextureTarget.Texture1DArray, mipMapLevels)
  
                GL.TexStorage2D(TextureTarget2d.Texture1DArray, mipMapLevels, unbox (int t), size, count)
                GL.Check "could not allocate texture"

                GL.BindTexture(TextureTarget.Texture1DArray, 0)
                GL.Check "could not unbind texture"
                
                tex.IsArray <- true
                tex.MipMapLevels <- mipMapLevels
                tex.Dimension <- TextureDimension.Texture1D
                tex.Count <- count
                tex.Multisamples <- 1
                tex.Size <- V3i(size, 1, 1)
                tex.Format <- t
                tex.ImmutableFormat <- true
            )

        member x.UpdateTexture2DArray(tex : Texture, size : V2i, count : int, mipMapLevels : int, t : TextureFormat, samples : int) =
            using x.ResourceLock (fun _ ->
                if tex.ImmutableFormat then
                    failwith "cannot update format/size for immutable texture"

                let target =
                    if samples = 1 then TextureTarget.Texture2DArray
                    else TextureTarget.Texture2DMultisampleArray

                GL.BindTexture(target, tex.Handle)
                GL.Check "could not bind texture"

                let sizeInBytes = texSizeInBytes(size.XYI, t, samples, mipMapLevels) * (int64 count)
                updateTexture tex.Context tex.SizeInBytes sizeInBytes
                tex.SizeInBytes <- sizeInBytes

                if samples = 1 then
                    x.SetDefaultTextureParams(target, mipMapLevels)
                    GL.TexStorage3D(TextureTarget3d.Texture2DArray, mipMapLevels, unbox (int t), size.X, size.Y, count)
                else
                    if mipMapLevels > 1 then failwith "multisampled textures cannot have MipMaps"
                    GL.TexStorage3DMultisample(TextureTargetMultisample3d.Texture2DMultisampleArray, samples, unbox (int t), size.X, size.Y, count, true)
  
                GL.Check "could not allocate texture"

                GL.BindTexture(target, 0)
                GL.Check "could not unbind texture"

                tex.MipMapLevels <- mipMapLevels
                tex.Dimension <- TextureDimension.Texture2D
                tex.IsArray <- true
                tex.Count <- count
                tex.Multisamples <- samples
                tex.Size <- V3i(size.X, size.Y, 1)
                tex.Format <- t
                tex.ImmutableFormat <- true
            )

        member x.UpdateTextureCubeArray(tex : Texture, size : int, count : int, mipMapLevels : int, t : TextureFormat, samples : int) =
            using x.ResourceLock (fun _ ->
                if tex.ImmutableFormat then
                    failwith "cannot update format/size for immutable texture"

                let target =
                    if samples = 1 then TextureTarget.TextureCubeMapArray
                    else
                        Log.warn "multi-sampled cube map array not supported"
                        failwith "multi-sampled cube map array not supported"

                GL.BindTexture(target, tex.Handle)
                GL.Check "could not bind texture"

                let sizeInBytes = texSizeInBytes(V3i(size, size, 1), t, samples, mipMapLevels) * 6L * (int64 count)
                updateTexture tex.Context tex.SizeInBytes sizeInBytes
                tex.SizeInBytes <- sizeInBytes

                if samples = 1 then
                    x.SetDefaultTextureParams(target, mipMapLevels)
                    GL.TexStorage3D(unbox (int target), mipMapLevels, unbox (int t), size, size, count * 6) // NOTE: there is no TextureTarget3d.TextureCubeMapArray ??
                else
                    if mipMapLevels > 1 then failwith "multisampled textures cannot have MipMaps"
                    failwith "not reachable"
  
                GL.Check "could not allocate texture"

                GL.BindTexture(target, 0)
                GL.Check "could not unbind texture"

                tex.MipMapLevels <- mipMapLevels
                tex.Dimension <- TextureDimension.TextureCube
                tex.IsArray <- true
                tex.Count <- count
                tex.Multisamples <- samples
                tex.Size <- V3i(size, size, 1)
                tex.Format <- t
                tex.ImmutableFormat <- true
            )

        member x.CreateTextureView(orig : Texture, levels : Range1i, slices : Range1i, isArray : bool) =
            using x.ResourceLock (fun _ ->
                if not orig.ImmutableFormat then
                    failwithf "cannot create texture-views for mutable textures"

                let handle = GL.GenTexture()
                GL.Check "could not create texture"

                let dim =
                    match orig.Dimension with
                        | TextureDimension.TextureCube -> 
                            if isArray || slices.Min = slices.Max then 
                                // address TextureCube or TextureCubeArray as Texture2d or Texture2dArray
                                TextureDimension.Texture2D
                            else
                                // address certain levels or single cube of cubeArray
                                if slices.Max - slices.Min + 1 <> 6 then failwithf "Creating multi-slice view (sliceCount>1 && sliceCount<>6) of CubeTexture(Array) requires isArray=true"
                                TextureDimension.TextureCube
                        | d -> d

                let levelCount = 1 + levels.Max - levels.Min
                let sliceCountHandle = if isArray then Some (1 + slices.Max - slices.Min)  else None
                let sliceCountCreate =
                    // create array if requested -> allows to create single views of array texture and an array view of a single texture
                    if isArray || orig.Dimension = TextureDimension.TextureCube && slices.Min <> slices.Max then Some (1 + slices.Max - slices.Min) 
                    else None
                    
                let tex = TextureViewHandle(x, handle, dim, levelCount, orig.Multisamples, orig.Size, sliceCountHandle, orig.Format)
                let target = TextureTarget.ofTexture tex
                  
                GL.TextureView(
                    handle,
                    target,
                    orig.Handle,
                    unbox (int orig.Format),
                    levels.Min, 1 + levels.Max - levels.Min,
                    slices.Min, match sliceCountCreate with | Some x -> x; | _ -> 1
                )
                GL.Check "could not create texture view"

                addTextureView x

                tex
            )

        member x.CreateTextureView(orig : Texture, levels : Range1i, slices : Range1i) =
            x.CreateTextureView(orig, levels, slices, orig.IsArray)

        member x.Delete(t : Texture) =
            using x.ResourceLock (fun _ ->
                match t with 
                | :? TextureViewHandle -> removeTextureView x
                | _ -> removeTexture x t.SizeInBytes
                GL.DeleteTexture(t.Handle)
                GL.Check "could not delete texture"
            )


[<AutoOpen>]
module TextureUploadExtensions =
    open Microsoft.FSharp.NativeInterop

    module private StructTypes = 
        [<StructLayout(LayoutKind.Explicit, Size = 1)>] type byte1 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 2)>] type byte2 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 3)>] type byte3 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 4)>] type byte4 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 5)>] type byte5 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 6)>] type byte6 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 7)>] type byte7 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 8)>] type byte8 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 9)>] type byte9 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 10)>] type byte10 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 11)>] type byte11 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 12)>] type byte12 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 13)>] type byte13 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 14)>] type byte14 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 15)>] type byte15 = struct end
        [<StructLayout(LayoutKind.Explicit, Size = 16)>] type byte16 = struct end


        let types = 
            Dictionary.ofList [
                1, typeof<byte1>
                2, typeof<byte2>
                3, typeof<byte3>
                4, typeof<byte4>
                5, typeof<byte5>
                6, typeof<byte6>
                7, typeof<byte7>
                8, typeof<byte8>
                9, typeof<byte9>
                10, typeof<byte10>
                11, typeof<byte11>
                12, typeof<byte12>
                13, typeof<byte13>
                14, typeof<byte14>
                15, typeof<byte15>
                16, typeof<byte16>
            ]

    [<AutoOpen>]
    module private ExistentialHack = 
        type IUnmanagedAction =
            abstract member Run<'a when 'a : unmanaged> : Option<'a> -> unit

        let private meth = typeof<IUnmanagedAction>.GetMethod "Run"

        let run (e : IUnmanagedAction) (t : Type) =
            let mi = meth.MakeGenericMethod [|t|]
            mi.Invoke(e, [| null |]) |> ignore

        type Col.Format with
            static member Stencil = unbox<Col.Format> (Int32.MaxValue)
            static member Depth = unbox<Col.Format> (Int32.MaxValue - 1)

        let toChannelCount =
            LookupTable.lookupTable [
                Col.Format.Alpha, 1
                Col.Format.BW, 1
                Col.Format.Gray, 1
                Col.Format.GrayAlpha, 2
                Col.Format.RGB, 3
                Col.Format.BGR, 3
                Col.Format.RGBA, 4
                Col.Format.BGRA, 4
                Col.Format.RGBP, 4
                Col.Format.NormalUV, 2
                Col.Format.Stencil, 1
                Col.Format.Depth, 1
            ]

    type TextureCopyUtils =

        static member Copy(elementType : Type, src : nativeint, srcInfo : VolumeInfo, dst : nativeint, dstInfo : VolumeInfo) =
            elementType |> run { 
                new IUnmanagedAction with
                    member x.Run(a : Option<'a>) =
                        let vSrc = NativeVolume<byte>(NativePtr.ofNativeInt src, srcInfo)
                        let vDst = NativeVolume<byte>(NativePtr.ofNativeInt dst, dstInfo)

                        let copy (s : nativeptr<byte>) (d : nativeptr<byte>) =
                            let s : nativeptr<'a> = NativePtr.cast s
                            let d : nativeptr<'a> = NativePtr.cast d
                            NativePtr.write d (NativePtr.read s)

                        NativeVolume.iter2 vSrc vDst copy
            }

        static member Copy(elementSize : int, src : nativeint, srcInfo : VolumeInfo, dst : nativeint, dstInfo : VolumeInfo) =
            TextureCopyUtils.Copy(StructTypes.types.[elementSize], src, srcInfo, dst, dstInfo)

        static member Copy(src : PixImage, dst : nativeint, dstInfo : VolumeInfo) =
            let gc = GCHandle.Alloc(src.Array, GCHandleType.Pinned)
            try
                let pSrc = gc.AddrOfPinnedObject()
                let imgInfo = src.VolumeInfo
                let elementType = src.PixFormat.Type
                let elementSize = elementType.GLSize |> int64
                let srcInfo =
                    VolumeInfo(
                        imgInfo.Origin * elementSize,
                        imgInfo.Size,
                        imgInfo.Delta * elementSize
                    )
                TextureCopyUtils.Copy(elementType, pSrc, srcInfo, dst, dstInfo)
            finally
                gc.Free()   

        static member Copy(src : nativeint, srcInfo : VolumeInfo, dst : PixImage) =
            let gc = GCHandle.Alloc(dst.Array, GCHandleType.Pinned)
            try
                let pDst = gc.AddrOfPinnedObject()
                let imgInfo = dst.VolumeInfo
                let elementType = dst.PixFormat.Type
                let elementSize = elementType.GLSize |> int64
                let dstInfo =
                    VolumeInfo(
                        imgInfo.Origin * elementSize,
                        imgInfo.Size,
                        imgInfo.Delta * elementSize
                    )
                TextureCopyUtils.Copy(elementType, src, srcInfo, pDst, dstInfo)
            finally
                gc.Free()     

    [<AutoOpen>]
    module FormatConversions = 
        type Col.Format with
            member x.ChannelCount = toChannelCount x

        module PixelFormat =
        
            let channels =
                LookupTable.lookupTable [
                    PixelFormat.Bgr, 3
                    PixelFormat.Bgra, 4
                    PixelFormat.Red, 1
                    PixelFormat.Rg, 2
                    PixelFormat.Rgb, 3
                    PixelFormat.Rgba, 4
                ]

            let ofColFormat =
                LookupTable.lookupTable [
                    Col.Format.Alpha, PixelFormat.Red
                    Col.Format.BGR, PixelFormat.Bgr
                    Col.Format.BGRA, PixelFormat.Bgra
                    Col.Format.BGRP, PixelFormat.Bgra
                    Col.Format.BW, PixelFormat.Red
                    Col.Format.Gray, PixelFormat.Red
                    Col.Format.GrayAlpha, PixelFormat.Rg
                    Col.Format.NormalUV, PixelFormat.Rg
                    Col.Format.RGB, PixelFormat.Rgb
                    Col.Format.RGBA, PixelFormat.Rgba
                    Col.Format.RGBP, PixelFormat.Rgba
                ]

        module PixelType =

            let size =
                LookupTable.lookupTable [
                    PixelType.UnsignedByte, 1
                    PixelType.Byte, 1
                    PixelType.UnsignedShort, 2
                    PixelType.Short, 2
                    PixelType.UnsignedInt, 4
                    PixelType.Int, 4
                    PixelType.HalfFloat, 2
                    PixelType.Float, 4
                ]

            let ofType =
                LookupTable.lookupTable [
                    typeof<uint8>, PixelType.UnsignedByte
                    typeof<int8>, PixelType.Byte
                    typeof<uint16>, PixelType.UnsignedShort
                    typeof<int16>, PixelType.Short
                    typeof<uint32>, PixelType.UnsignedInt
                    typeof<int32>, PixelType.Int
                    typeof<float16>, PixelType.HalfFloat
                    typeof<float32>, PixelType.Float
                ]

            let compressedFormat =
                LookupTable.lookupTable' [
                    (PixelFormat.Rgb, PixelType.UnsignedByte, false), (TextureFormat.CompressedRgbS3tcDxt1Ext, PixelInternalFormat.CompressedRgbS3tcDxt1Ext)
                    (PixelFormat.Rgba, PixelType.UnsignedByte, false), (TextureFormat.CompressedRgbaS3tcDxt5Ext, PixelInternalFormat.CompressedRgbaS3tcDxt5Ext)
                    (PixelFormat.Rgb, PixelType.UnsignedByte, true), (TextureFormat.CompressedSrgbS3tcDxt1Ext, PixelInternalFormat.CompressedSrgbS3tcDxt1Ext)
                    (PixelFormat.Rgba, PixelType.UnsignedByte, true), (TextureFormat.CompressedSrgbAlphaS3tcDxt5Ext, PixelInternalFormat.CompressedSrgbAlphaS3tcDxt5Ext)
                    (PixelFormat.Bgr, PixelType.UnsignedByte, false), (TextureFormat.CompressedRgbS3tcDxt1Ext, PixelInternalFormat.CompressedRgbS3tcDxt1Ext)
                    (PixelFormat.Bgra, PixelType.UnsignedByte, false), (TextureFormat.CompressedRgbaS3tcDxt5Ext, PixelInternalFormat.CompressedRgbaS3tcDxt5Ext)
                    (PixelFormat.Bgr, PixelType.UnsignedByte, true), (TextureFormat.CompressedSrgbS3tcDxt1Ext, PixelInternalFormat.CompressedSrgbS3tcDxt1Ext)
                    (PixelFormat.Bgra, PixelType.UnsignedByte, true), (TextureFormat.CompressedSrgbAlphaS3tcDxt5Ext, PixelInternalFormat.CompressedSrgbAlphaS3tcDxt5Ext)
                    (PixelFormat.Luminance, PixelType.UnsignedByte, false), (TextureFormat.CompressedRedRgtc1, PixelInternalFormat.CompressedRedRgtc1)
                ]

        module TextureFormat =

            let toFormatAndType =
                LookupTable.lookupTable [
                    TextureFormat.Bgr8 , (PixelFormat.Bgr, PixelType.UnsignedByte)
                    TextureFormat.Bgra8 , (PixelFormat.Bgra, PixelType.UnsignedByte)
                    TextureFormat.Rgb8 , (PixelFormat.Rgb, PixelType.UnsignedByte)
                    TextureFormat.Rgb16 , (PixelFormat.Rgb, PixelType.UnsignedShort)
                    TextureFormat.Rgba8 , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.Rgb10A2 , (PixelFormat.Rgba, PixelType.UnsignedInt1010102)
                    TextureFormat.Rgba16 , (PixelFormat.Rgba, PixelType.UnsignedShort)

                    TextureFormat.DepthComponent16 , (PixelFormat.DepthComponent, PixelType.HalfFloat)
                    TextureFormat.DepthComponent24 , (PixelFormat.DepthComponent, PixelType.Float)
                    TextureFormat.DepthComponent32 , (PixelFormat.DepthComponent, PixelType.Float)
                    TextureFormat.CompressedRed , (PixelFormat.Red, PixelType.UnsignedByte)
                    TextureFormat.CompressedRg , (PixelFormat.Rg, PixelType.UnsignedByte)
                    TextureFormat.R8 , (PixelFormat.Red, PixelType.UnsignedByte)
                    TextureFormat.R16 , (PixelFormat.Red, PixelType.UnsignedShort)
                    TextureFormat.Rg8 , (PixelFormat.Rg, PixelType.UnsignedByte)
                    TextureFormat.Rg16 , (PixelFormat.Rg, PixelType.UnsignedShort)
                    TextureFormat.R16f , (PixelFormat.Red, PixelType.HalfFloat)
                    TextureFormat.R32f , (PixelFormat.Red, PixelType.Float)
                    TextureFormat.Rg16f , (PixelFormat.Rg, PixelType.HalfFloat)
                    TextureFormat.Rg32f , (PixelFormat.Rg, PixelType.Float)
                    TextureFormat.R8i , (PixelFormat.Red, PixelType.Byte)
                    TextureFormat.R8ui , (PixelFormat.Red, PixelType.UnsignedByte)
                    TextureFormat.R16i , (PixelFormat.Red, PixelType.Short)
                    TextureFormat.R16ui , (PixelFormat.Red, PixelType.UnsignedShort)
                    TextureFormat.R32i , (PixelFormat.Red, PixelType.Int)
                    TextureFormat.R32ui , (PixelFormat.Red, PixelType.UnsignedInt)
                    TextureFormat.Rg8i , (PixelFormat.Rg, PixelType.Byte)
                    TextureFormat.Rg8ui , (PixelFormat.Rg, PixelType.UnsignedByte)
                    TextureFormat.Rg16i , (PixelFormat.Rg, PixelType.Short)
                    TextureFormat.Rg16ui , (PixelFormat.Rg, PixelType.UnsignedShort)
                    TextureFormat.Rg32i , (PixelFormat.Rg, PixelType.Int)
                    TextureFormat.Rg32ui , (PixelFormat.Rg, PixelType.UnsignedInt)
                    TextureFormat.CompressedRgbS3tcDxt1Ext , (PixelFormat.Rgb, PixelType.UnsignedByte)
                    TextureFormat.CompressedRgbaS3tcDxt1Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.CompressedRgbaS3tcDxt3Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.CompressedRgbaS3tcDxt5Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.CompressedAlpha , (PixelFormat.Alpha, PixelType.UnsignedByte)
                    TextureFormat.CompressedLuminance , (PixelFormat.Luminance, PixelType.UnsignedByte)
                    TextureFormat.CompressedLuminanceAlpha , (PixelFormat.LuminanceAlpha, PixelType.UnsignedByte)
                    TextureFormat.CompressedRgb , (PixelFormat.Rgb, PixelType.UnsignedByte)
                    TextureFormat.CompressedRgba , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.DepthStencil , (PixelFormat.DepthStencil, PixelType.Float32UnsignedInt248Rev)

                    TextureFormat.Rgba32f , (PixelFormat.Rgba, PixelType.Float)
                    TextureFormat.Rgb32f , (PixelFormat.Rgb, PixelType.Float)
                    TextureFormat.Rgba16f , (PixelFormat.Rgba, PixelType.HalfFloat)
                    TextureFormat.Rgb16f , (PixelFormat.Rgb, PixelType.HalfFloat)
                    TextureFormat.Depth24Stencil8 , (PixelFormat.DepthComponent, PixelType.Float32UnsignedInt248Rev)
                    TextureFormat.Srgb , (PixelFormat.Rgb, PixelType.UnsignedByte)
                    TextureFormat.Srgb8 , (PixelFormat.Rgb, PixelType.UnsignedByte)
                    TextureFormat.SrgbAlpha , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.Srgb8Alpha8 , (PixelFormat.Rgba, PixelType.UnsignedByte)

                    TextureFormat.CompressedSrgb , (PixelFormat.Rgb, PixelType.UnsignedByte)
                    TextureFormat.CompressedSrgbAlpha , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.CompressedSrgbS3tcDxt1Ext , (PixelFormat.Rgb, PixelType.UnsignedByte)
                    TextureFormat.CompressedSrgbAlphaS3tcDxt1Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.CompressedSrgbAlphaS3tcDxt3Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.CompressedSrgbAlphaS3tcDxt5Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.DepthComponent32f , (PixelFormat.DepthComponent, PixelType.Float)
                    TextureFormat.Depth32fStencil8 , (PixelFormat.DepthComponent, PixelType.Float)
                    TextureFormat.Rgba32ui , (PixelFormat.Rgba, PixelType.UnsignedInt)
                    TextureFormat.Rgb32ui , (PixelFormat.Rgb, PixelType.UnsignedInt)
                    TextureFormat.Rgba16ui , (PixelFormat.Rgba, PixelType.UnsignedShort)
                    TextureFormat.Rgb16ui , (PixelFormat.Rgb, PixelType.UnsignedShort)
                    TextureFormat.Rgba8ui , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.Rgb8ui , (PixelFormat.Rgb, PixelType.UnsignedByte)
                    TextureFormat.Rgba32i , (PixelFormat.Rgba, PixelType.Int)
                    TextureFormat.Rgb32i , (PixelFormat.Rgb, PixelType.Int)
                    TextureFormat.Rgba16i , (PixelFormat.Rgba, PixelType.Short)
                    TextureFormat.Rgb16i , (PixelFormat.Rgb, PixelType.Short)
                    TextureFormat.Rgba8i , (PixelFormat.Rgba, PixelType.Byte)
                    TextureFormat.Rgb8i , (PixelFormat.Rgb, PixelType.Byte)
                    TextureFormat.Float32UnsignedInt248Rev , (PixelFormat.DepthComponent, PixelType.Float32UnsignedInt248Rev)
                    TextureFormat.CompressedRedRgtc1 , (PixelFormat.Red, PixelType.UnsignedByte)
                    TextureFormat.CompressedSignedRedRgtc1 , (PixelFormat.Red, PixelType.Byte)
                    TextureFormat.CompressedRgRgtc2 , (PixelFormat.Rg, PixelType.UnsignedByte)
                    TextureFormat.CompressedSignedRgRgtc2 , (PixelFormat.Rg, PixelType.Byte)
                    TextureFormat.CompressedRgbaBptcUnorm , (PixelFormat.Rgba, PixelType.UnsignedByte)
                    TextureFormat.CompressedRgbBptcSignedFloat , (PixelFormat.Rgb, PixelType.Float)
                    TextureFormat.CompressedRgbBptcUnsignedFloat , (PixelFormat.Rgb, PixelType.Float)
                    TextureFormat.R8Snorm , (PixelFormat.Red, PixelType.Byte)
                    TextureFormat.Rg8Snorm , (PixelFormat.Rg, PixelType.Byte)
                    TextureFormat.Rgb8Snorm , (PixelFormat.Rgb, PixelType.Byte)
                    TextureFormat.Rgba8Snorm , (PixelFormat.Rgba, PixelType.Byte)
                    TextureFormat.R16Snorm , (PixelFormat.Red, PixelType.Short)
                    TextureFormat.Rg16Snorm , (PixelFormat.Rg, PixelType.Short)
                    TextureFormat.Rgb16Snorm , (PixelFormat.Rgb, PixelType.Short)
                    TextureFormat.Rgba16Snorm , (PixelFormat.Rgba, PixelType.Short)
                ]

    [<RequireQualifiedAccess>]
    type internal PixelUnpackBuffer =
        | Native of int
        | Host of nativeptr<byte>

    module internal PixelUnpackBuffer =

        let private createNative (usage : OpenTK.Graphics.OpenGL4.BufferUsageHint) (sizeInBytes : nativeint) =
            let pbo = GL.GenBuffer()
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pbo)
            GL.Check "could not bind PBO"

            GL.BufferData(BufferTarget.PixelUnpackBuffer, sizeInBytes, 0n, usage)
            GL.Check "could not initialize PBO"

            PixelUnpackBuffer.Native pbo

        let private createHost (sizeInBytes : nativeint) =
            PixelUnpackBuffer.Host <| NativePtr.alloc<byte> (int sizeInBytes)

        let create (usage : OpenTK.Graphics.OpenGL4.BufferUsageHint) (sizeInBytes : nativeint) =
            if GL.Config.UsePixelUnpackBuffers then createNative usage sizeInBytes
            else createHost sizeInBytes

        let map (access : BufferAccess) = function
            | PixelUnpackBuffer.Native _ ->
                let dst = GL.MapBuffer(BufferTarget.PixelUnpackBuffer, access)
                GL.Check "could not map PBO"
                dst

            | PixelUnpackBuffer.Host ptr ->
                NativePtr.toNativeInt ptr

        let unmap = function
            | PixelUnpackBuffer.Native _ ->
                let worked = GL.UnmapBuffer(BufferTarget.PixelUnpackBuffer)
                if not worked then Log.warn "[GL] could not unmap buffer"
                GL.Check "could not unmap PBO"

                0n

            | PixelUnpackBuffer.Host ptr ->
                NativePtr.toNativeInt ptr

        let free = function
            | PixelUnpackBuffer.Native pbo ->
                GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0)
                GL.Check "could not unbind PBO"

                GL.DeleteBuffer(pbo)
                GL.Check "could not delete PBO"

            | PixelUnpackBuffer.Host ptr ->
                NativePtr.free ptr

    type PixImage with
        member image.PinPBO(align : int, trafo : ImageTrafo, f : V2i -> PixelType -> PixelFormat -> nativeint -> nativeint -> unit) =
            let pt = PixelType.ofType image.PixFormat.Type
            let pf = PixelFormat.ofColFormat image.Format
            
            let align = align |> nativeint
            let mask = align - 1n |> nativeint
            let size = image.Size
            let elementSize = image.PixFormat.Type.GLSize
            let channels = toChannelCount image.Format

            let lineSize = nativeint size.X * nativeint elementSize * nativeint channels

            let alignedLineSize =
                if lineSize % align = 0n then lineSize
                else (lineSize + mask) &&& ~~~mask

            let sizeInBytes = alignedLineSize * nativeint size.Y

            let pbo = PixelUnpackBuffer.create BufferUsageHint.StaticDraw sizeInBytes
            let dst = pbo |> PixelUnpackBuffer.map BufferAccess.WriteOnly

            let dstInfo =
                let viSize = V3l(int64 size.X, int64 size.Y, int64 channels)
                match trafo with
                    | ImageTrafo.Identity -> 
                        VolumeInfo(
                            0L,
                            viSize,
                            V3l(int64 channels * int64 elementSize, int64 alignedLineSize, int64 elementSize)
                        )

                    | ImageTrafo.MirrorY -> 
                        VolumeInfo(
                            int64 alignedLineSize * (int64 size.Y - 1L),
                            viSize,
                            V3l(int64 channels * int64 elementSize, int64 -alignedLineSize, int64 elementSize)
                        )

                    | ImageTrafo.MirrorX ->
                        VolumeInfo(
                            int64 size.X - 1L,
                            viSize,
                            V3l(int64 -channels * int64 elementSize, int64 alignedLineSize, int64 elementSize)
                        )

                    | ImageTrafo.Rot180 ->
                        VolumeInfo(
                            int64 alignedLineSize * (int64 size.Y - 1L) + int64 size.X - 1L,
                            viSize,
                            V3l(int64 -channels * int64 elementSize, int64 -alignedLineSize, int64 elementSize)
                        )

                    | _ ->
                        failwithf "[GL] only supports ImageTrafo.[Rot0|MirrorY|MirrorX|Rot180] atm. but got %A" trafo

            TextureCopyUtils.Copy(image, dst, dstInfo)

            let pixels = pbo |> PixelUnpackBuffer.unmap
            f size pt pf pixels sizeInBytes

            pbo |> PixelUnpackBuffer.free

    type PixVolume with
        member x.PinPBO(align : int, f : V3i -> PixelType -> PixelFormat -> nativeint -> nativeint -> unit) =
            let size = x.Size
            let pt = PixelType.ofType x.PixFormat.Type
            let pf = PixelFormat.ofColFormat x.Format

            let align = align |> nativeint
            let alignMask = align - 1n |> nativeint
            let channelSize = x.PixFormat.Type.GLSize |> nativeint
            let channels = toChannelCount x.Format |> nativeint

            let pixelSize = channelSize * channels

            let rowSize = pixelSize * nativeint size.X
            let alignedRowSize = (rowSize + (alignMask - 1n)) &&& ~~~alignMask
            let sizeInBytes = alignedRowSize * nativeint size.Y * nativeint size.Z

            let pbo = PixelUnpackBuffer.create BufferUsageHint.StaticDraw sizeInBytes
            let pDst = pbo |> PixelUnpackBuffer.map BufferAccess.WriteOnly

            if alignedRowSize % channelSize <> 0n then
                failwith "[GL] unexpected row alignment (not implemented atm.)"

            let dstInfo =
                let rowPixels = alignedRowSize / channelSize
                let viSize = V4l(int64 size.X, int64 size.Y, int64 size.Z, int64 channels)
                Tensor4Info(
                    0L,
                    viSize,
                    V4l(
                        int64 channels, 
                        int64 rowPixels, 
                        int64 rowPixels * viSize.Y, 
                        1L
                    )
                )

            let elementType = x.PixFormat.Type

            elementType |> ExistentialHack.run {
                new IUnmanagedAction with
                    member __.Run(def : Option<'a>) =
                        let x = unbox<PixVolume<'a>> x
                        let dst = NativeTensor4<'a>(NativePtr.ofNativeInt pDst, dstInfo)
                        NativeTensor4.using x.Tensor4 (fun src ->
                            src.CopyTo(dst)
                        )
            }

            let pixels = pbo |> PixelUnpackBuffer.unmap
            f size pt pf pixels sizeInBytes

            pbo |> PixelUnpackBuffer.free

    type PBOInfo =
        {
            size        : V3i
            flags       : BufferStorageFlags
            pixelFormat : PixelFormat
            pixelType   : PixelType
        }

    module NativeTensor4 =

        let private pixelFormat =
            LookupTable.lookupTable [
                1L, PixelFormat.Red
                2L, PixelFormat.Rg
                3L, PixelFormat.Rgb
                4L, PixelFormat.Rgba
            ]

        let withPBO (x : NativeTensor4<'a>) (align : int) (f : V3i -> PixelType -> PixelFormat -> nativeint -> nativeint -> unit) =
            let size = x.Info.Size
            let pt = PixelType.ofType typeof<'a>
            let pf = pixelFormat size.W
            
            let align = align |> nativeint
            let alignMask = align - 1n |> nativeint
            let channelSize = typeof<'a>.GLSize |> nativeint
            let channels = size.W |> nativeint

            let pixelSize = channelSize * channels

            let rowSize = pixelSize * nativeint size.X
            let alignedRowSize = (rowSize + (alignMask - 1n)) &&& ~~~alignMask
            let sizeInBytes = alignedRowSize * nativeint size.Y * nativeint size.Z

            if alignedRowSize % channelSize <> 0n then
                failwith "[GL] unexpected row alignment (not implemented atm.)"

            let dstInfo =
                let rowPixels = alignedRowSize / channelSize
                let viSize = V4l(int64 size.X, int64 size.Y, int64 size.Z, int64 channels)
                Tensor4Info(
                    0L,
                    viSize,
                    V4l(
                        int64 channels, 
                        int64 rowPixels, 
                        int64 rowPixels * viSize.Y, 
                        1L
                    )
                )

            let pbo = PixelUnpackBuffer.create BufferUsageHint.StaticDraw sizeInBytes
            let pDst = pbo |> PixelUnpackBuffer.map BufferAccess.WriteOnly

            let dst = NativeTensor4<'a>(NativePtr.ofNativeInt pDst, dstInfo)
            x.CopyTo(dst)

            let pixels = pbo |> PixelUnpackBuffer.unmap

            f (V3i size.XYZ) pt pf pixels sizeInBytes

            pbo |> PixelUnpackBuffer.free

        let usePBO (info : PBOInfo) (align : int) (mapping : int -> nativeint -> Tensor4Info -> 'r) =
            let size = info.size
            let pt = info.pixelType
            let pf = info.pixelFormat
            
            let align = align |> nativeint
            let alignMask = align - 1n |> nativeint
            let channelSize = PixelType.size pt |> nativeint
            let channels = PixelFormat.channels pf |> nativeint

            let pixelSize = channelSize * channels

            let rowSize = pixelSize * nativeint size.X
            let alignedRowSize = (rowSize + (alignMask - 1n)) &&& ~~~alignMask
            let sizeInBytes = alignedRowSize * nativeint size.Y * nativeint size.Z
            
            if alignedRowSize % channelSize <> 0n then
                failwith "[GL] unexpected row alignment (not implemented atm.)"

            let srcInfo =
                let rowPixels = alignedRowSize / channelSize
                let viSize = V4l(int64 size.X, int64 size.Y, int64 size.Z, int64 channels)
                Tensor4Info(
                    0L,
                    viSize,
                    V4l(
                        int64 channels, 
                        int64 rowPixels, 
                        int64 rowPixels * viSize.Y, 
                        1L
                    )
                )
                
            let pbo = GL.GenBuffer()
            GL.BindBuffer(BufferTarget.CopyWriteBuffer, pbo)
            GL.Check "could not bind PBO"
            GL.BufferStorage(BufferTarget.CopyWriteBuffer, sizeInBytes, 0n, BufferStorageFlags.MapReadBit)
            GL.Check "could not allocate PBO"
            GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0)

            try 
                mapping pbo sizeInBytes srcInfo

            finally
                GL.DeleteBuffer(pbo)
                GL.Check "could not delete PBO"
                

[<AutoOpen>]
module TextureExtensions =

    [<AutoOpen>]
    module internal Patterns =

        let compressedFormats =
            HashSet.ofList [
                TextureFormat.CompressedRed
                TextureFormat.CompressedRg
                TextureFormat.CompressedRgbS3tcDxt1Ext
                TextureFormat.CompressedRgbaS3tcDxt1Ext
                TextureFormat.CompressedRgbaS3tcDxt3Ext
                TextureFormat.CompressedRgbaS3tcDxt5Ext
                TextureFormat.CompressedAlpha
                TextureFormat.CompressedLuminance
                TextureFormat.CompressedLuminanceAlpha
                TextureFormat.CompressedIntensity
                TextureFormat.CompressedRgb
                TextureFormat.CompressedRgba
                TextureFormat.CompressedSrgb
                TextureFormat.CompressedSrgbAlpha
                TextureFormat.CompressedSluminance
                TextureFormat.CompressedSluminanceAlpha
                TextureFormat.CompressedSrgbS3tcDxt1Ext
                TextureFormat.CompressedSrgbAlphaS3tcDxt1Ext
                TextureFormat.CompressedSrgbAlphaS3tcDxt3Ext
                TextureFormat.CompressedSrgbAlphaS3tcDxt5Ext
                TextureFormat.CompressedRedRgtc1
                TextureFormat.CompressedSignedRedRgtc1
                TextureFormat.CompressedRgRgtc2
                TextureFormat.CompressedSignedRgRgtc2
                TextureFormat.CompressedRgbaBptcUnorm
                TextureFormat.CompressedRgbBptcSignedFloat
                TextureFormat.CompressedRgbBptcUnsignedFloat
            ]

        let (|FileTexture|_|) (t : ITexture) =
            match t with
                | :? FileTexture as t -> Some(FileTexture(t.TextureParams, t.FileName))
                | _ -> None

        let (|PixTextureCube|_|) (t : ITexture) =
            match t with
                | :? PixTextureCube as t -> Some(PixTextureCube(t.TextureParams, t.PixImageCube))
                | _ -> None

        let (|PixTexture2D|_|) (t : ITexture) =
            match t with
                | :? PixTexture2d as t -> Some(t.TextureParams, t.PixImageMipMap)
                | _ -> None

        let (|PixTexture3D|_|) (t : ITexture) =
            match t with
                | :? PixTexture3d as t -> Some(PixTexture3D(t.TextureParams, t.PixVolume))
                | _ -> None
                
    type Col.Format with
        static member Stencil = unbox<Col.Format> (Int32.MaxValue)
        static member Depth = unbox<Col.Format> (Int32.MaxValue - 1)
        static member DepthStencil = unbox<Col.Format> (Int32.MaxValue - 2)

    let internal toPixelType =
        LookupTable.lookupTable' [
            typeof<uint8>, PixelType.UnsignedByte
            typeof<int8>, PixelType.Byte
            typeof<uint16>, PixelType.UnsignedShort
            typeof<int16>, PixelType.Short
            typeof<uint32>, PixelType.UnsignedInt
            typeof<int32>, PixelType.Int
            typeof<float32>, PixelType.Float
            typeof<float16>, PixelType.HalfFloat
        ]

    let internal toPixelFormat =
        let table =
            LookupTable.lookupTable' [
                Col.Format.Alpha, PixelFormat.Alpha
                Col.Format.BW, PixelFormat.Red
                Col.Format.Gray, PixelFormat.Red
                Col.Format.GrayAlpha, PixelFormat.Rg
                Col.Format.RGB, PixelFormat.Rgb
                Col.Format.BGR, PixelFormat.Bgr
                Col.Format.RGBA, PixelFormat.Rgba
                Col.Format.BGRA, PixelFormat.Bgra
                Col.Format.RGBP, PixelFormat.Rgba
                Col.Format.NormalUV, PixelFormat.Rg
                Col.Format.Stencil, PixelFormat.StencilIndex
                Col.Format.Depth, PixelFormat.DepthComponent
                Col.Format.DepthStencil, PixelFormat.DepthComponent
            ]

        let getVersion() =
            Version(GL.GetInteger(GetPName.MajorVersion), GL.GetInteger(GetPName.MinorVersion), 0)

        fun input ->
            match input |> table with
            | Some PixelFormat.StencilIndex when getVersion() < Version(4, 4, 0) ->
                Log.error "GL_STENCIL_INDEX requires OpenGL 4.4 or higher"
                None

            | fmt ->
                fmt

    let toUntypedPixelFormat =
        LookupTable.lookupTable' [
            TextureFormat.DepthComponent16, PixelFormat.DepthComponent
            TextureFormat.Depth24Stencil8, PixelFormat.DepthComponent
            TextureFormat.DepthComponent32, PixelFormat.DepthComponent
            TextureFormat.DepthComponent32f, PixelFormat.DepthComponent

            TextureFormat.Rgba8, PixelFormat.Rgba
            TextureFormat.Rgba16, PixelFormat.Rgba
            TextureFormat.Rgba16f, PixelFormat.Rgba
            TextureFormat.Rgba32f, PixelFormat.Rgba

            TextureFormat.Rgb8, PixelFormat.Rgb
            TextureFormat.Rgb16, PixelFormat.Rgb
            TextureFormat.Rgb16f, PixelFormat.Rgb
            TextureFormat.Rgb32f, PixelFormat.Rgb

            TextureFormat.Rg8, PixelFormat.Rg
            TextureFormat.Rg16, PixelFormat.Rg
            TextureFormat.Rg16f, PixelFormat.Rg
            TextureFormat.Rg32f, PixelFormat.Rg

            TextureFormat.R8, PixelFormat.Red
            TextureFormat.R16, PixelFormat.Red
            TextureFormat.R16f, PixelFormat.Red
            TextureFormat.R32f, PixelFormat.Red

            TextureFormat.CompressedRgbS3tcDxt1Ext, PixelFormat.Rgb
            TextureFormat.CompressedRgbaS3tcDxt5Ext, PixelFormat.Rgba
            TextureFormat.CompressedSrgbS3tcDxt1Ext, PixelFormat.Rgb
            TextureFormat.CompressedSrgbAlphaS3tcDxt5Ext, PixelFormat.Rgba
        ]

    let internal toChannelCount =
        LookupTable.lookupTable' [
            Col.Format.Alpha, 1
            Col.Format.BW, 1
            Col.Format.Gray, 1
            Col.Format.GrayAlpha, 2
            Col.Format.RGB, 3
            Col.Format.BGR, 3
            Col.Format.RGBA, 4
            Col.Format.BGRA, 4
            Col.Format.RGBP, 4
            Col.Format.NormalUV, 2
            Col.Format.Stencil, 1
            Col.Format.Depth, 1
        ]


    let internal toFormatAndType =
        LookupTable.lookupTable [
            TextureFormat.Bgr8 , (PixelFormat.Bgr, PixelType.UnsignedByte)
            TextureFormat.Bgra8 , (PixelFormat.Bgra, PixelType.UnsignedByte)
            TextureFormat.Rgb8 , (PixelFormat.Rgb, PixelType.UnsignedByte)
            TextureFormat.Rgb16 , (PixelFormat.Rgb, PixelType.UnsignedShort)
            TextureFormat.Rgba8 , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.Rgb10A2 , (PixelFormat.Rgba, PixelType.UnsignedInt1010102)
            TextureFormat.Rgba16 , (PixelFormat.Rgba, PixelType.UnsignedShort)

            TextureFormat.DepthComponent16 , (PixelFormat.DepthComponent, PixelType.HalfFloat)
            TextureFormat.DepthComponent24 , (PixelFormat.DepthComponent, PixelType.Float)
            TextureFormat.DepthComponent32 , (PixelFormat.DepthComponent, PixelType.Float)
            TextureFormat.CompressedRed , (PixelFormat.Red, PixelType.UnsignedByte)
            TextureFormat.CompressedRg , (PixelFormat.Rg, PixelType.UnsignedByte)
            TextureFormat.R8 , (PixelFormat.Red, PixelType.UnsignedByte)
            TextureFormat.R16 , (PixelFormat.Red, PixelType.UnsignedShort)
            TextureFormat.Rg8 , (PixelFormat.Rg, PixelType.UnsignedByte)
            TextureFormat.Rg16 , (PixelFormat.Rg, PixelType.UnsignedShort)
            TextureFormat.R16f , (PixelFormat.Red, PixelType.HalfFloat)
            TextureFormat.R32f , (PixelFormat.Red, PixelType.Float)
            TextureFormat.Rg16f , (PixelFormat.Rg, PixelType.HalfFloat)
            TextureFormat.Rg32f , (PixelFormat.Rg, PixelType.Float)
            TextureFormat.R8i , (PixelFormat.Red, PixelType.Byte)
            TextureFormat.R8ui , (PixelFormat.Red, PixelType.UnsignedByte)
            TextureFormat.R16i , (PixelFormat.Red, PixelType.Short)
            TextureFormat.R16ui , (PixelFormat.Red, PixelType.UnsignedShort)
            TextureFormat.R32i , (PixelFormat.Red, PixelType.Int)
            TextureFormat.R32ui , (PixelFormat.Red, PixelType.UnsignedInt)
            TextureFormat.Rg8i , (PixelFormat.Rg, PixelType.Byte)
            TextureFormat.Rg8ui , (PixelFormat.Rg, PixelType.UnsignedByte)
            TextureFormat.Rg16i , (PixelFormat.Rg, PixelType.Short)
            TextureFormat.Rg16ui , (PixelFormat.Rg, PixelType.UnsignedShort)
            TextureFormat.Rg32i , (PixelFormat.Rg, PixelType.Int)
            TextureFormat.Rg32ui , (PixelFormat.Rg, PixelType.UnsignedInt)
            TextureFormat.CompressedRgbS3tcDxt1Ext , (PixelFormat.Rgb, PixelType.UnsignedByte)
            TextureFormat.CompressedRgbaS3tcDxt1Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.CompressedRgbaS3tcDxt3Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.CompressedRgbaS3tcDxt5Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.CompressedAlpha , (PixelFormat.Alpha, PixelType.UnsignedByte)
            TextureFormat.CompressedLuminance , (PixelFormat.Luminance, PixelType.UnsignedByte)
            TextureFormat.CompressedLuminanceAlpha , (PixelFormat.LuminanceAlpha, PixelType.UnsignedByte)
            TextureFormat.CompressedRgb , (PixelFormat.Rgb, PixelType.UnsignedByte)
            TextureFormat.CompressedRgba , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.DepthStencil , (PixelFormat.DepthStencil, PixelType.Float32UnsignedInt248Rev)

            TextureFormat.Rgba32f , (PixelFormat.Rgba, PixelType.Float)
            TextureFormat.Rgb32f , (PixelFormat.Rgb, PixelType.Float)
            TextureFormat.Rgba16f , (PixelFormat.Rgba, PixelType.HalfFloat)
            TextureFormat.Rgb16f , (PixelFormat.Rgb, PixelType.HalfFloat)
            TextureFormat.Depth24Stencil8 , (PixelFormat.DepthComponent, PixelType.Float32UnsignedInt248Rev)
            TextureFormat.Srgb , (PixelFormat.Rgb, PixelType.UnsignedByte)
            TextureFormat.Srgb8 , (PixelFormat.Rgb, PixelType.UnsignedByte)
            TextureFormat.SrgbAlpha , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.Srgb8Alpha8 , (PixelFormat.Rgba, PixelType.UnsignedByte)

            TextureFormat.CompressedSrgb , (PixelFormat.Rgb, PixelType.UnsignedByte)
            TextureFormat.CompressedSrgbAlpha , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.CompressedSrgbS3tcDxt1Ext , (PixelFormat.Rgb, PixelType.UnsignedByte)
            TextureFormat.CompressedSrgbAlphaS3tcDxt1Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.CompressedSrgbAlphaS3tcDxt3Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.CompressedSrgbAlphaS3tcDxt5Ext , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.DepthComponent32f , (PixelFormat.DepthComponent, PixelType.Float)
            TextureFormat.Depth32fStencil8 , (PixelFormat.DepthComponent, PixelType.Float)
            TextureFormat.Rgba32ui , (PixelFormat.Rgba, PixelType.UnsignedInt)
            TextureFormat.Rgb32ui , (PixelFormat.Rgb, PixelType.UnsignedInt)
            TextureFormat.Rgba16ui , (PixelFormat.Rgba, PixelType.UnsignedShort)
            TextureFormat.Rgb16ui , (PixelFormat.Rgb, PixelType.UnsignedShort)
            TextureFormat.Rgba8ui , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.Rgb8ui , (PixelFormat.Rgb, PixelType.UnsignedByte)
            TextureFormat.Rgba32i , (PixelFormat.Rgba, PixelType.Int)
            TextureFormat.Rgb32i , (PixelFormat.Rgb, PixelType.Int)
            TextureFormat.Rgba16i , (PixelFormat.Rgba, PixelType.Short)
            TextureFormat.Rgb16i , (PixelFormat.Rgb, PixelType.Short)
            TextureFormat.Rgba8i , (PixelFormat.Rgba, PixelType.Byte)
            TextureFormat.Rgb8i , (PixelFormat.Rgb, PixelType.Byte)
            TextureFormat.Float32UnsignedInt248Rev , (PixelFormat.DepthComponent, PixelType.Float32UnsignedInt248Rev)
            TextureFormat.CompressedRedRgtc1 , (PixelFormat.Red, PixelType.UnsignedByte)
            TextureFormat.CompressedSignedRedRgtc1 , (PixelFormat.Red, PixelType.Byte)
            TextureFormat.CompressedRgRgtc2 , (PixelFormat.Rg, PixelType.UnsignedByte)
            TextureFormat.CompressedSignedRgRgtc2 , (PixelFormat.Rg, PixelType.Byte)
            TextureFormat.CompressedRgbaBptcUnorm , (PixelFormat.Rgba, PixelType.UnsignedByte)
            TextureFormat.CompressedRgbBptcSignedFloat , (PixelFormat.Rgb, PixelType.Float)
            TextureFormat.CompressedRgbBptcUnsignedFloat , (PixelFormat.Rgb, PixelType.Float)
            TextureFormat.R8Snorm , (PixelFormat.Red, PixelType.Byte)
            TextureFormat.Rg8Snorm , (PixelFormat.Rg, PixelType.Byte)
            TextureFormat.Rgb8Snorm , (PixelFormat.Rgb, PixelType.Byte)
            TextureFormat.Rgba8Snorm , (PixelFormat.Rgba, PixelType.Byte)
            TextureFormat.R16Snorm , (PixelFormat.Red, PixelType.Short)
            TextureFormat.Rg16Snorm , (PixelFormat.Rg, PixelType.Short)
            TextureFormat.Rgb16Snorm , (PixelFormat.Rgb, PixelType.Short)
            TextureFormat.Rgba16Snorm , (PixelFormat.Rgba, PixelType.Short)

        ]

    let compressionRatio = 
         LookupTable.lookupTable [
                TextureFormat.CompressedRgbS3tcDxt1Ext, 6
                TextureFormat.CompressedRgbaS3tcDxt1Ext, 6
                TextureFormat.CompressedRgbaS3tcDxt3Ext, 4 
                TextureFormat.CompressedRgbaS3tcDxt5Ext, 4
                TextureFormat.CompressedSrgbS3tcDxt1Ext, 6
                TextureFormat.CompressedSrgbAlphaS3tcDxt1Ext, 6
                TextureFormat.CompressedSrgbAlphaS3tcDxt3Ext, 4
                TextureFormat.CompressedSrgbAlphaS3tcDxt5Ext, 4
                TextureFormat.CompressedRedRgtc1, 4
                TextureFormat.CompressedSignedRedRgtc1, 4
                TextureFormat.CompressedRgRgtc2, 4
                TextureFormat.CompressedSignedRgRgtc2, 4
                TextureFormat.CompressedRgbaBptcUnorm, 4
                TextureFormat.CompressedRgbBptcSignedFloat, 4
                TextureFormat.CompressedRgbBptcUnsignedFloat, 4
         ]

    module internal Devil =
        open DevILSharp

        let private pixelType =
            LookupTable.lookupTable' [
                ChannelType.Byte, PixelType.Byte
                //ChannelType.Double, PixelType.Double
                ChannelType.Float, PixelType.Float
                ChannelType.Half, PixelType.HalfFloat
                ChannelType.Int, PixelType.Int
                ChannelType.Short, PixelType.Short
                ChannelType.UnsignedByte, PixelType.UnsignedByte
                ChannelType.UnsignedInt, PixelType.UnsignedInt
                ChannelType.UnsignedShort, PixelType.UnsignedShort
            ]

        let private pixelFormat =
            LookupTable.lookupTable' [
                ChannelFormat.RGB, PixelFormat.Rgb
                ChannelFormat.BGR, PixelFormat.Bgr
                ChannelFormat.RGBA, PixelFormat.Rgba
                ChannelFormat.BGRA, PixelFormat.Bgra
                ChannelFormat.Luminance, PixelFormat.Luminance
                ChannelFormat.Alpha, PixelFormat.Alpha
                ChannelFormat.LuminanceAlpha, PixelFormat.LuminanceAlpha

            ]

        let private compressedFormat =
            LookupTable.lookupTable' [
                (ChannelFormat.RGB, ChannelType.UnsignedByte, false), (CompressedDataFormat.Dxt1, PixelInternalFormat.CompressedRgbS3tcDxt1Ext)
                (ChannelFormat.RGBA, ChannelType.UnsignedByte, false), (CompressedDataFormat.Dxt5, PixelInternalFormat.CompressedRgbaS3tcDxt5Ext)
                (ChannelFormat.RGB, ChannelType.UnsignedByte, true), (CompressedDataFormat.Dxt1, PixelInternalFormat.CompressedSrgbS3tcDxt1Ext)
                (ChannelFormat.RGBA, ChannelType.UnsignedByte, true), (CompressedDataFormat.Dxt5, PixelInternalFormat.CompressedSrgbAlphaS3tcDxt5Ext)
                
                (ChannelFormat.BGR, ChannelType.UnsignedByte, false), (CompressedDataFormat.Dxt1, PixelInternalFormat.CompressedRgbS3tcDxt1Ext)
                (ChannelFormat.BGRA, ChannelType.UnsignedByte, false), (CompressedDataFormat.Dxt5, PixelInternalFormat.CompressedRgbaS3tcDxt5Ext)
                (ChannelFormat.BGR, ChannelType.UnsignedByte, true), (CompressedDataFormat.Dxt1, PixelInternalFormat.CompressedSrgbS3tcDxt1Ext)
                (ChannelFormat.BGRA, ChannelType.UnsignedByte, true), (CompressedDataFormat.Dxt5, PixelInternalFormat.CompressedSrgbAlphaS3tcDxt5Ext)
            
                (ChannelFormat.Luminance, ChannelType.UnsignedByte, false), (CompressedDataFormat.Dxt1, PixelInternalFormat.CompressedRedRgtc1)
            ]

        module private PixFormat =
            let private types =
                LookupTable.lookupTable' [
                    ChannelType.Byte, typeof<int8>
                    //ChannelType.Double, PixelType.Double
                    ChannelType.Float, typeof<float32>
                    ChannelType.Half, typeof<float16>
                    ChannelType.Int, typeof<int>
                    ChannelType.Short, typeof<int16>
                    ChannelType.UnsignedByte, typeof<uint8>
                    ChannelType.UnsignedInt, typeof<uint32>
                    ChannelType.UnsignedShort, typeof<uint16>
                ]

            let private colFormat =
                LookupTable.lookupTable' [
                    ChannelFormat.RGB, Col.Format.RGB
                    ChannelFormat.BGR, Col.Format.BGR
                    ChannelFormat.RGBA, Col.Format.RGBA
                    ChannelFormat.BGRA, Col.Format.BGRA
                    ChannelFormat.Luminance, Col.Format.Gray
                    ChannelFormat.Alpha, Col.Format.Alpha
                    ChannelFormat.LuminanceAlpha, Col.Format.GrayAlpha
                ]

            let get(fmt : ChannelFormat, t : ChannelType) =
                match types t, colFormat fmt with
                    | Some t, Some fmt -> PixFormat(t, fmt) |> Some
                    | _ -> None

        let devilLock =
            let fi = typeof<PixImageDevil>.GetField("s_devilLock", Reflection.BindingFlags.Static ||| Reflection.BindingFlags.NonPublic)
            fi.GetValue(null)

        let uploadTexture2DLevelFile (t : Texture) (level : int) (file : string) (config : TextureParams) =
            lock devilLock (fun () ->
                PixImageDevil.InitDevil()
                let img = IL.GenImage()
                try
                    IL.BindImage(img)
                    IL.LoadImage(file) |> IL.check "could not load image"
                

                
                    let w = IL.GetInteger(IntName.ImageWidth)
                    let h = IL.GetInteger(IntName.ImageHeight)
                    let fmt = IL.GetInteger(IntName.ImageFormat) |> unbox<DevILSharp.ChannelFormat>
                    let pt = IL.GetDataType()

                    let compressedFormat =
                        if config.wantCompressed then
                            match compressedFormat(fmt, pt, config.wantSrgb) with
                                | Some t -> Some t
                                | _ -> None
                        else
                            None


                    match compressedFormat with
                        | Some (fmt, ifmt) ->
                            ILU.FlipImage() |> IL.check "could not flip image"
                            let channels = IL.GetInteger(IntName.ImageChannels)
                            let size = IL.GetDXTCData(0n, 0, fmt)
                
                            Log.line "compression: %.2f%%" (100.0 * float size / float (w * h * channels)) 

                            let pbo = PixelUnpackBuffer.create BufferUsageHint.StaticDraw (nativeint size)
                            let ptr = pbo |> PixelUnpackBuffer.map BufferAccess.WriteOnly
   
                            IL.GetDXTCData(ptr, size, fmt) |> ignore

                            let pixels = pbo |> PixelUnpackBuffer.unmap

                            GL.BindTexture(TextureTarget.Texture2D, t.Handle)
                            GL.Check "[uploadTexture2DLevelFile] BindTexture"

                            GL.CompressedTexImage2D(TextureTarget.Texture2D, level, unbox<InternalFormat> (int ifmt), w, h, 0, size, pixels)
                            GL.Check "[uploadTexture2DLevelFile] CompressedTexImage2D"
                            GL.BindTexture(TextureTarget.Texture2D, 0)

                            pbo |> PixelUnpackBuffer.free

                            updateTexture t.Context t.SizeInBytes (int64 size)
                            t.Format <- unbox (int ifmt)
                            t.SizeInBytes <- int64 size

                        | _ ->
                            match PixFormat.get(fmt, pt) with
                                | Some pixFormat ->
                                    let ifmt = TextureFormat.ofPixFormat pixFormat config

                                    let pixelType, pixelFormat =
                                        match toPixelType pixFormat.Type, toPixelFormat pixFormat.Format with
                                            | Some t, Some f -> (t,f)
                                            | _ ->
                                                failwith "conversion not implemented"


                                    let elementSize = pixFormat.Type.GLSize
                                    let channelCount =
                                        match toChannelCount pixFormat.Format with
                                            | Some c -> c
                                            | _ -> pixFormat.ChannelCount
                                
                                    let align = t.Context.PackAlignment
                                    let lineSize = w * elementSize * channelCount
                                    let alignedLineSize =
                                        if lineSize % align = 0 then lineSize
                                        else (lineSize + (align - 1)) &&& ~~~(align - 1)


                                    let size = int64 (alignedLineSize * h)
                                    let pbo = PixelUnpackBuffer.create BufferUsageHint.StaticDraw (nativeint size)
                                    let dst = pbo |> PixelUnpackBuffer.map BufferAccess.WriteOnly

                                    let src = IL.GetData()

                                    let d = channelCount * elementSize

                                    let srcInfo =
                                        VolumeInfo(
                                            0L, 
                                            V3l(int64 w, int64 h, int64 d),
                                            V3l(int64 d, int64 lineSize, 1L)
                                        )

                                    let dstInfo = 
                                        VolumeInfo(
                                            int64 alignedLineSize * (srcInfo.SY-1L), 
                                            srcInfo.Size, 
                                            V3l(srcInfo.DX, int64 -alignedLineSize, srcInfo.DZ)
                                        )

                                    let vSrc = 
                                        NativeVolume<byte>(
                                            NativePtr.ofNativeInt src, 
                                            srcInfo
                                        )

                                    let vDst = 
                                        NativeVolume<byte>(
                                            NativePtr.ofNativeInt dst, 
                                            dstInfo
                                        )

                                    NativeVolume.iter2 vSrc vDst (fun src dst -> NativePtr.write dst (NativePtr.read src))
                                    let pixels = pbo |> PixelUnpackBuffer.unmap

                                    GL.BindTexture(TextureTarget.Texture2D, t.Handle)
                                    GL.TexImage2D(TextureTarget.Texture2D, level, unbox (int ifmt), w, h, 0, pixelFormat, pixelType, pixels)
                                    GL.Check "[uploadTexture2DLevelFile] TexImage2D"
                                    GL.BindTexture(TextureTarget.Texture2D, 0)

                                    pbo |> PixelUnpackBuffer.free

                                    updateTexture t.Context t.SizeInBytes size
                                    t.SizeInBytes <- size
                                    t.Format <- ifmt


                                | _ -> 
                                    failwith "[GL] could not get PixFormat for devil-texture"




                    t.Size <- V3i(w,h,1)
                    t.Dimension <- TextureDimension.Texture2D
                    t.Count <- 1
                    t.ImmutableFormat <- false
                    t.MipMapLevels <- 1
                    t.Multisamples <- 1
                    IL.BindImage(0)
                finally
                    IL.DeleteImage(img)
            )

        let uploadTexture2D (t : Texture) (file : string) (config : TextureParams) =
            uploadTexture2DLevelFile t 0 file config
            GL.Check "uploadTexture2D"

            GL.BindTexture(TextureTarget.Texture2D, t.Handle)
            GL.Check "BindTexture"
            if config.wantMipMaps then
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D)
                GL.Check "GenerateMipmap"
                let newSize = 4L * t.SizeInBytes / 3L
                updateTexture t.Context t.SizeInBytes newSize
                t.SizeInBytes <- newSize
            else
                GL.TexParameterI(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, [| 0 |])
                GL.TexParameterI(TextureTarget.Texture2D, TextureParameterName.TextureMaxLod, [| 0 |])
                GL.Check "TexParameterI"
            GL.BindTexture(TextureTarget.Texture2D, 0)



    [<AutoOpen>]
    module internal PixelTransfer =
        let getTextureTarget' (dim : TextureDimension) (isArray : bool) (isMS : bool) =
            match dim, isArray, isMS with
            | TextureDimension.Texture1D,      _,       true     -> failwith "Texture1D cannot be multisampled"
            | TextureDimension.Texture1D,      true,    _        -> TextureTarget.Texture1DArray
            | TextureDimension.Texture1D,      false,   _        -> TextureTarget.Texture1D

            | TextureDimension.Texture2D,      false,   false    -> TextureTarget.Texture2D
            | TextureDimension.Texture2D,      true,    false    -> TextureTarget.Texture2DArray
            | TextureDimension.Texture2D,      false,   true     -> TextureTarget.Texture2DMultisample
            | TextureDimension.Texture2D,      true,    true     -> TextureTarget.Texture2DMultisampleArray

            | TextureDimension.Texture3D,      false,   false    -> TextureTarget.Texture3D
            | TextureDimension.Texture3D,      _,       _        -> failwith "Texture3D cannot be multisampled or an array"

            | TextureDimension.TextureCube,   false,    false    -> TextureTarget.TextureCubeMap
            | TextureDimension.TextureCube,   true,     false    -> TextureTarget.TextureCubeMapArray
            | TextureDimension.TextureCube,   _,        true     -> failwith "TextureCube cannot be multisampled"

            | _ -> failwithf "unknown texture dimension: %A" dim

        let getTextureTarget (texture : Texture) =
            getTextureTarget' texture.Dimension texture.IsArray texture.IsMultisampled

        let getTextureSliceTarget (slice : int) (texture : Texture) =
            match getTextureTarget texture with
            | TextureTarget.TextureCubeMap -> cubeSides.[slice] |> snd
            | target -> target

        let private uploadTexture2DInternal (bindTarget : TextureTarget) (target : TextureTarget) (isTopLevel : bool) (t : Texture) (startLevel : int) (textureParams : TextureParams) (data : PixImageMipMap) =
            if data.LevelCount <= 0 then
                failwith "cannot upload texture having 0 levels"

            let size = data.[0].Size
            let expectedLevels = 1 + max size.X size.Y |> Fun.Log2 |> Fun.Floor |> int
            let uploadLevels = if textureParams.wantMipMaps then data.LevelCount else 1
            let generateMipMap = textureParams.wantMipMaps && data.LevelCount < expectedLevels
            
            let pixelType = PixelType.ofType data.PixFormat.Type 
            let pixelFormat = PixelFormat.ofColFormat data.PixFormat.Format 

            let compressedFormat =
                        if textureParams.wantCompressed then
                            match PixelType.compressedFormat (pixelFormat, pixelType, textureParams.wantSrgb) with
                                | Some t -> Some t
                                | _ -> Log.warn "[GL] Texture format (%A, %A) does not support compression" pixelFormat pixelType; None
                        else
                            None

            let newFormat = match compressedFormat with
                            | Some fmt -> fmt
                            | _ -> let textureFormat = TextureFormat.ofPixFormat data.[0].PixFormat textureParams
                                   let internalFormat = TextureFormat.ofPixFormat data.[0].PixFormat textureParams |> int |> unbox<PixelInternalFormat>
                                   (textureFormat, internalFormat)

            let internalFormat = snd newFormat
            let newFormat = fst newFormat

            let formatChanged = t.Format <> newFormat
            let isCompressed = compressedFormats.Contains newFormat
            let oldIsCompressed = compressedFormats.Contains  t.Format
            t.Format <- newFormat

            let sizeChanged = size <> t.Size2D
            let compressionChanged = oldIsCompressed <> isCompressed
            if sizeChanged || compressionChanged then
                let texFormatUncompressed = TextureFormat.ofPixFormat data.[0].PixFormat TextureParams.empty
                let sizeInBytes = texSizeInBytes(size.XYI, texFormatUncompressed, 1, 1)
                let sizeInBytes = if textureParams.wantMipMaps then (sizeInBytes <<< 2) / 3L else sizeInBytes
                let sizeInBytes = if isCompressed then 
                                    let ratio = compressionRatio newFormat
                                    sizeInBytes / int64 ratio
                                  else
                                    sizeInBytes
                updateTexture t.Context t.SizeInBytes sizeInBytes
                t.SizeInBytes <- sizeInBytes

            GL.BindTexture(bindTarget, t.Handle)
            GL.Check "could not bind texture"

            if not generateMipMap then
                GL.TexParameterI(bindTarget, TextureParameterName.TextureMaxLevel, [|uploadLevels - 1|])
                GL.TexParameterI(bindTarget, TextureParameterName.TextureBaseLevel, [| 0 |])

            for l in 0..uploadLevels-1 do
                let image = data.[l]

                image.PinPBO(t.Context.PackAlignment,ImageTrafo.MirrorY, fun dim pixelType pixelFormat pixels size ->
                    if sizeChanged || formatChanged then
                        GL.TexImage2D(target, startLevel + l, internalFormat, dim.X, dim.Y, 0, pixelFormat, pixelType, pixels)
                    else
                        GL.TexSubImage2D(target, startLevel + l, 0, 0, dim.X, dim.Y, pixelFormat, pixelType, pixels)
                    GL.Check (sprintf "could not upload texture data for level %d" l)
                )
                
            // if the image did not contain a sufficient
            // number of MipMaps and the user demanded 
            // MipMaps we generate them using OpenGL
            if generateMipMap && isTopLevel then
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D)
                GL.Check "failed to generate mipmaps"

            GL.BindTexture(bindTarget, 0)
            GL.Check "could not bind texture"

            // since some attributes of the texture
            // may have changed we mutate them here
            if isTopLevel then
                t.Size <- V3i(size.X, size.Y, 0)
                t.Multisamples <- 1
                t.Count <- 1
                t.Dimension <- TextureDimension.Texture2D
                //t.ChannelType <- ChannelType.fromGlFormat internalFormat

            generateMipMap

        let uploadTexture2D (t : Texture) (textureParams : TextureParams) (data : PixImageMipMap) =
            uploadTexture2DInternal TextureTarget.Texture2D TextureTarget.Texture2D true t 0 textureParams data |> ignore

        let uploadTextureCube (t : Texture) (textureParams : TextureParams) (data : PixImageCube) =
            for (s,_) in cubeSides do
                if data.[s].LevelCount <= 0 then
                    failwith "cannot upload texture having 0 levels"

            let mutable generateMipMaps = false
            let size = data.[CubeSide.NegativeX].[0].Size

            let mutable minLevels = Int32.MaxValue
            for (side, target) in cubeSides do
                let data = data.[side]
                
                minLevels <- min minLevels data.LevelCount
                let generate = uploadTexture2DInternal TextureTarget.TextureCubeMap target false t 0 textureParams data

                if generate && textureParams.wantMipMaps then
                    generateMipMaps <- true

            let realSize = t.SizeInBytes * 6L
            updateTexture t.Context t.SizeInBytes realSize
            t.SizeInBytes <- realSize

            let levels =
                if generateMipMaps then
                    GL.BindTexture(TextureTarget.TextureCubeMap, t.Handle)
                    GL.Check "could not bind texture"

                    GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap)
                    GL.Check "failed to generate mipmaps"

                    GL.BindTexture(TextureTarget.TextureCubeMap, 0)
                    GL.Check "could not unbind texture"

                    GL.GetTexParameterI(TextureTarget.TextureCubeMap, GetTextureParameter.TextureMaxLevel, &minLevels)
                    minLevels + 1
                else
                    GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, minLevels - 1)
                    GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureBaseLevel, 0)

                    minLevels
                
            t.MipMapLevels <- levels
            t.Size <- V3i(size.X, size.Y, 0)
            t.Multisamples <- 1
            t.Count <- 1
            t.Dimension <- TextureDimension.TextureCube


        let uploadTexture3D (t : Texture) (textureParams : TextureParams) (data : PixVolume) =
            let size = data.Size
            let expectedLevels = 1 + Fun.Max(size.X, size.Y, size.Z) |> Fun.Log2 |> Fun.Floor |> int
            let generateMipMap = textureParams.wantMipMaps
            let newFormat = TextureFormat.ofPixFormat data.PixFormat textureParams
            let formatChanged = t.Format <> newFormat
            let sizeChanged = size <> t.Size3D
            let internalFormat = TextureFormat.ofPixFormat data.PixFormat textureParams |> int |> unbox<PixelInternalFormat>

            GL.BindTexture(TextureTarget.Texture3D, t.Handle)
            GL.Check "could not bind texture"

            data.PinPBO (t.Context.PackAlignment, fun size pt pf pixels sizeInBytes ->
                if sizeChanged || formatChanged then
                    if not generateMipMap then
                        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLod, 0)
                        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureBaseLevel, 0)

                    GL.TexImage3D(TextureTarget.Texture3D, 0, internalFormat, size.X, size.Y, size.Z, 0, pf, pt, pixels)
                else
                    GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, size.X, size.Y, size.Z, pf, pt, pixels)
                GL.Check "could not upload texture data"
            )

            // if the image did not contain a sufficient
            // number of MipMaps and the user demanded 
            // MipMaps we generate them using OpenGL
            if generateMipMap then
                GL.GenerateMipmap(GenerateMipmapTarget.Texture3D)
                GL.Check "failed to generate mipmaps"

            GL.BindTexture(TextureTarget.Texture3D, 0)
            GL.Check "could not bind texture"

            // since some attributes of the texture
            // may have changed we mutate them here
            t.Size <- size
            t.Multisamples <- 1
            t.Count <- 1
            t.MipMapLevels <- (if generateMipMap then expectedLevels else 1)
            t.Dimension <- TextureDimension.Texture3D
            t.Format <- newFormat

        let uploadNativeTexture (t : Texture) (data : INativeTexture) =
            match data.Dimension, data.Count with
                | TextureDimension.Texture2D, 1 ->
                    let target = TextureTarget.Texture2D
                    GL.BindTexture(target, t.Handle)

                    let isCompressed = compressedFormats.Contains data.Format

                    let mutable totalSize = 0L
                    for l in 0 .. data.MipMapLevels - 1 do
                        let levelData = data.[0,l]

                        totalSize <- totalSize + levelData.SizeInBytes
                        levelData.Use(fun src ->
                            let pbo = PixelUnpackBuffer.create BufferUsageHint.StaticDraw (nativeint levelData.SizeInBytes)
                            let dst = pbo |> PixelUnpackBuffer.map BufferAccess.WriteOnly

                            Marshal.Copy(src, dst, levelData.SizeInBytes)

                            let pixels = pbo |> PixelUnpackBuffer.unmap

                            if isCompressed then
                                GL.CompressedTexImage2D(target, l, unbox (int data.Format), levelData.Size.X, levelData.Size.Y, 0, int levelData.SizeInBytes, pixels)
                            else
                                let pf, pt = toFormatAndType data.Format
                                GL.TexImage2D(target, l, unbox (int data.Format), levelData.Size.X, levelData.Size.Y, 0, pf, pt, pixels)

                            pbo |> PixelUnpackBuffer.free
                        )

                    GL.TexParameterI(target, TextureParameterName.TextureMaxLevel, [|data.MipMapLevels - 1|])

                    updateTexture t.Context t.SizeInBytes totalSize
                    t.SizeInBytes <- totalSize
                    t.Count <- 1
                    t.Dimension <- data.Dimension
                    t.Format <- data.Format
                    t.ImmutableFormat <- false
                    t.MipMapLevels <- data.MipMapLevels
                    t.Multisamples <- 1
                    GL.BindTexture(target, 0)
                | _ ->

                    failwith "implement me"
            ()

        let private readTexture2D (texture : Texture) (targetSlice : TextureTarget) (level : int) (slice : int)
                                  (offset : V2i) (size : V2i) (pixelFormat : PixelFormat)
                                  (pixelType : PixelType) (dst : nativeint) =
            let fbo = GL.GenFramebuffer()
            GL.Check "could not create framebuffer"

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo)
            GL.Check "could not bind framebuffer"

            let attachment, readBuffer =
                if TextureFormat.isDepthStencil texture.Format then
                    FramebufferAttachment.DepthStencilAttachment, ReadBufferMode.None
                elif TextureFormat.isDepth texture.Format then
                    FramebufferAttachment.DepthAttachment, ReadBufferMode.None
                else
                    FramebufferAttachment.ColorAttachment0, ReadBufferMode.ColorAttachment0

            if texture.IsArray then
                GL.FramebufferTextureLayer(FramebufferTarget.ReadFramebuffer, attachment, texture.Handle, level, slice)
            else
                GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, attachment, targetSlice, texture.Handle, level)

            GL.Check "could not attach texture to framebuffer"

            let fboCheck = GL.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer)
            if fboCheck <> FramebufferErrorCode.FramebufferComplete then
                failwithf "could not create input framebuffer: %A" fboCheck

            GL.ReadBuffer(readBuffer)
            GL.Check "could not set readbuffer"

            GL.ReadPixels(offset.X, offset.Y, size.X, size.Y, pixelFormat, pixelType, dst)
            GL.Check "could not read pixels"

            GL.ReadBuffer(ReadBufferMode.None)
            GL.Check "could not unset readbuffer"

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0)
            GL.Check "could not unbind framebuffer"

            GL.DeleteFramebuffer(fbo)
            GL.Check "could not delete framebuffer"


        let downloadTexture2D (texture : Texture) (level : int) (slice : int) (offset : V2i) (image : PixImage) =
            let format = image.PixFormat
            let target =  texture |> getTextureTarget
            let targetSlice =  texture |> getTextureSliceTarget slice
            GL.BindTexture(target, texture.Handle)
            GL.Check "could not bind texture"

            let pixelType, pixelFormat =
                match toPixelType format.Type, toPixelFormat image.Format with
                | Some t, Some f -> (t, f)
                | _ ->
                    failwith "conversion not implemented"

            let elementSize = image.PixFormat.Type.GLSize
            let channelCount =
                match toChannelCount image.Format with
                | Some c -> c
                | _ -> image.PixFormat.ChannelCount

            let lineSize = image.Size.X * channelCount * elementSize
            let packAlign = texture.Context.PackAlignment

            let alignedLineSize = (lineSize + (packAlign - 1)) &&& ~~~(packAlign - 1)
            let targetSize = alignedLineSize * image.Size.Y

            let buffer = NativePtr.alloc<byte> targetSize

            try
                let src = NativePtr.toNativeInt buffer

                // In case we download the whole texture and it isn't arrayed, we can
                // avoid GL.GetTextureSubImage() which is not available on all systems! (e.g. MacOS)
                if offset = V2i.Zero && image.Size = texture.GetSize(level).XY && not texture.IsArray then
                    GL.GetTexImage(targetSlice, level, pixelFormat, pixelType, src)
                else
                    if GL.ARB_get_texture_subimage then
                        GL.GetTextureSubImage(texture.Handle, level, offset.X, offset.Y, slice,
                                              image.Size.X, image.Size.Y, 1,
                                              pixelFormat, pixelType, targetSize, src)

                    // Use readPixels with FBO as fallback
                    else
                        readTexture2D texture targetSlice level slice offset image.Size pixelFormat pixelType src

                GL.Check "could not get texture image"

                let dstInfo = image.VolumeInfo
                let dy = int64(alignedLineSize / elementSize)
                let srcInfo =
                    VolumeInfo(
                        dy * (dstInfo.Size.Y - 1L),
                        dstInfo.Size,
                        V3l(dstInfo.SZ, -dy, 1L)
                    )

                NativeVolume.copyNativeToImage src srcInfo image

            finally
                NativePtr.free buffer

            GL.BindTexture(target, 0)
            GL.Check "could not unbind texture"

        let downloadTexture (texture : Texture) (level : int) (slice : int) (offset : V2i) (image : PixImage) =
            match texture.Dimension with
            | TextureDimension.Texture2D
            | TextureDimension.TextureCube ->
                downloadTexture2D texture level slice offset image

            | _ ->
                failwithf "cannot download textures of kind: %A" texture.Dimension

[<Extension; AbstractClass; Sealed>]
type ContextTextureExtensions =

    [<Extension>]
    static member CreateTexture(this : Context, data : ITexture) =
        using this.ResourceLock (fun _ ->
            let newTexture () = // not all cases need new textures
                let h = GL.GenTexture()
                GL.Check "could not create texture"
                addTexture this 0L
                Texture(this, h, TextureDimension.Texture2D, 1, 1, V3i(-1,-1,-1), None, TextureFormat.Rgba8, 0L, false)

            match data with

                | FileTexture(info, file) ->
                    let t = newTexture ()
                    if isNull file then 
                        t
                    else
                        if info.wantCompressed then
                            Devil.uploadTexture2D t file info
                        else
                            let pi = PixImage.Create(file, PixLoadOptions.UseDevil)
                            let mm = PixImageMipMap [|pi|] 
                            uploadTexture2D t info mm |> ignore
                        t

                | PixTexture2D(wantMipMaps, data) -> 
                    let t = newTexture ()
                    uploadTexture2D t wantMipMaps data |> ignore
                    t

                | PixTextureCube(info, data) ->
                    let t = newTexture () 
                    uploadTextureCube t info data
                    t

                | PixTexture3D(info, data) ->
                    let t = newTexture ()
                    uploadTexture3D t info data
                    t

                | :? NullTexture ->
                    Texture(this, 0, TextureDimension.Texture2D, 1, 1, V3i(-1,-1,-1), None, TextureFormat.Rgba8, 0L, false)

                | :? Texture as o ->
                    o

                | :? INativeTexture as data ->
                    let t = newTexture () 
                    uploadNativeTexture t data
                    t

                | _ ->
                    failwith "unsupported texture data"

        )

    [<Extension>]
    static member Upload(this : Context, t : Texture, data : ITexture) =
        using this.ResourceLock (fun _ ->
            match data with
                | PixTexture2D(wantMipMaps, data) -> 
                    uploadTexture2D t wantMipMaps data |> ignore

                | PixTextureCube(info, data) -> 
                    uploadTextureCube t info data

                | PixTexture3D(info, image) ->
                    uploadTexture3D t info image

                | FileTexture(info, file) ->
                    Devil.uploadTexture2D t file info

                | :? NullTexture -> failwith "cannot update texture with null texture"

                | :? Texture as o ->
                    if t.Handle <> o.Handle then
                        failwith "cannot upload to framebuffer-texture"

                | :? INativeTexture as data ->
                    uploadNativeTexture t data

                | _ ->
                    failwith "unsupported texture data"
        )

    [<Extension>]
    static member Blit(this : Context,
                       src : Texture, srcLevel : int, srcSlice : int, srcOffset : V2i, srcSize : V2i,
                       dst : Texture, dstLevel : int, dstSlice : int, dstOffset : V2i, dstSize : V2i,
                       linear : bool) =
        this.Blit(src, srcLevel, srcSlice, Box2i.FromMinAndSize(srcOffset, srcSize),
                  dst, dstLevel, dstSlice, Box2i.FromMinAndSize(dstOffset, dstSize),
                  linear)

    [<Extension>]
    static member Blit(this : Context, src : Texture, srcLevel : int, srcSlice : int, srcRegion : Box2i, dst : Texture, dstLevel : int, dstSlice : int, dstRegion : Box2i, linear : bool) =
        using this.ResourceLock (fun _ ->
            let fSrc = GL.GenFramebuffer()
            GL.Check "could not create framebuffer"
            let fDst = GL.GenFramebuffer()
            GL.Check "could not create framebuffer"

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fSrc)
            GL.Check "could not bind framebuffer"

            let attachment, mask, linear =
                if TextureFormat.isDepthStencil src.Format then
                    FramebufferAttachment.DepthStencilAttachment, ClearBufferMask.DepthBufferBit ||| ClearBufferMask.StencilBufferBit, false
                elif TextureFormat.isDepth src.Format then
                    FramebufferAttachment.DepthAttachment, ClearBufferMask.DepthBufferBit, false
                else
                    FramebufferAttachment.ColorAttachment0, ClearBufferMask.ColorBufferBit, linear

            if src.Slices > 1 then GL.FramebufferTextureLayer(FramebufferTarget.ReadFramebuffer, attachment, src.Handle, srcLevel, srcSlice)
            else GL.FramebufferTexture(FramebufferTarget.ReadFramebuffer, attachment, src.Handle, srcLevel)
            GL.Check "could not attach texture to framebuffer"

            let srcCheck = GL.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer)
            if srcCheck <> FramebufferErrorCode.FramebufferComplete then
                failwithf "could not create input framebuffer: %A" srcCheck

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fDst)
            GL.Check "could not bind framebuffer"

            if dst.Slices > 1 then GL.FramebufferTextureLayer(FramebufferTarget.DrawFramebuffer, attachment, dst.Handle, dstLevel, dstSlice)
            else GL.FramebufferTexture(FramebufferTarget.DrawFramebuffer, attachment, dst.Handle, dstLevel)
            GL.Check "could not attach texture to framebuffer"

            let dstCheck = GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer)
            if dstCheck <> FramebufferErrorCode.FramebufferComplete then
                failwithf "could not create output framebuffer: %A" dstCheck

            GL.BlitFramebuffer(
                srcRegion.Min.X, srcRegion.Min.Y,
                srcRegion.Max.X, srcRegion.Max.Y,

                dstRegion.Min.X, dstRegion.Min.Y,
                dstRegion.Max.X, dstRegion.Max.Y,

                mask,
                (if linear then BlitFramebufferFilter.Linear else BlitFramebufferFilter.Nearest)
            )
            GL.Check "could blit framebuffer"

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0)
            GL.Check "could unbind framebuffer"

            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0)
            GL.Check "could unbind framebuffer"

            GL.DeleteFramebuffer(fSrc)
            GL.Check "could delete framebuffer"

            GL.DeleteFramebuffer(fDst)
            GL.Check "could delete framebuffer"

        )

    [<Extension>]
    static member Copy(this : Context, src : Texture, srcLevel : int, srcSlice : int, srcOffset : V2i, dst : Texture, dstLevel : int, dstSlice : int, dstOffset : V2i, size : V2i) =
        using this.ResourceLock (fun _ ->
            let fSrc = GL.GenFramebuffer()
            GL.Check "could not create framebuffer"

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fSrc)
            GL.Check "could not bind framebuffer"

            let attachment, readBuffer =
                if TextureFormat.isDepthStencil src.Format then
                    FramebufferAttachment.DepthStencilAttachment, ReadBufferMode.None
                elif TextureFormat.isDepth src.Format then
                    FramebufferAttachment.DepthAttachment, ReadBufferMode.None
                else
                    FramebufferAttachment.ColorAttachment0, ReadBufferMode.ColorAttachment0

            if src.Slices > 1 then
                GL.FramebufferTextureLayer(FramebufferTarget.ReadFramebuffer, attachment, src.Handle, srcLevel, srcSlice)
            else
                GL.FramebufferTexture(FramebufferTarget.ReadFramebuffer, attachment, src.Handle, srcLevel)
            GL.Check "could not attach texture to framebuffer"

            let srcCheck = GL.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer)
            if srcCheck <> FramebufferErrorCode.FramebufferComplete then
                failwithf "could not create input framebuffer: %A" srcCheck

            GL.ReadBuffer(readBuffer)
            GL.Check "could not set readbuffer"


            let bindTarget = getTextureTarget dst
            GL.BindTexture(bindTarget, dst.Handle)
            GL.Check "could not bind texture"

            // NOTE: according to glCopyTexSubImage2D/3D documentation: multi-sampled texture are not supported
            if dst.IsArray then

                GL.CopyTexSubImage3D(
                    bindTarget,
                    dstLevel,
                    dstOffset.X, dstOffset.Y, dstSlice,
                    srcOffset.X, srcOffset.Y,
                    size.X, size.Y
                )
                GL.Check "could not copy texture"

            else
                
                let copyTarget =
                    match dst.Dimension with
                        | TextureDimension.TextureCube -> snd cubeSides.[dstSlice]
                        | _ -> TextureTarget.Texture2D

                GL.CopyTexSubImage2D(
                    copyTarget,
                    dstLevel,
                    dstOffset.X, dstOffset.Y,
                    srcOffset.X, srcOffset.Y,
                    size.X, size.Y
                )
                GL.Check "could not copy texture"


            GL.ReadBuffer(ReadBufferMode.None)
            GL.Check "could not unset readbuffer"

            GL.BindTexture(bindTarget, 0)
            GL.Check "could not unbind texture"

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0)
            GL.Check "could not unbind framebuffer"

            GL.DeleteFramebuffer(fSrc)
            GL.Check "could not delete framebuffer"

        )

    [<Extension>]
    static member Upload(this : Context, t : Texture, level : int, slice : int, offset : V2i, source : PixImage) =
        using this.ResourceLock (fun _ ->
            let levelSize = t.GetSize level
            let offset = V2i(offset.X, levelSize.Y - offset.Y - source.Size.Y) // flip y-offset
            if offset = V2i.Zero && source.Size = levelSize.XY then
                this.Upload(t, level, slice, source)
            else
                let temp = this.CreateTexture2D(source.Size, 1, t.Format, 1)

                try
                    this.Upload(temp, 0, 0, source)

                    if t.IsMultisampled then // resolve multisamples
                        this.Blit(temp, 0, 0, V2i.Zero, source.Size, t, level, slice, offset, source.Size, true)
                    else
                        this.Copy(temp, 0, 0, V2i.Zero, t, level, slice, offset, source.Size)
                finally
                    this.Delete(temp)
        )

    [<Extension>]
    static member Upload(this : Context, t : Texture, level : int, slice : int, source : PixImage) =
        using this.ResourceLock (fun _ ->
            match t.Dimension with
                | TextureDimension.Texture2D -> 
                    let target = getTextureTarget t
                    GL.BindTexture(target, t.Handle)
                    GL.Check "could not bind texture"

                    source.PinPBO(t.Context.PackAlignment, ImageTrafo.MirrorY, fun dim pixelType pixelFormat pixels size ->
                        if target = TextureTarget.Texture2DArray then
                            GL.TexSubImage3D(target, level, 0, 0, slice, dim.X, dim.Y, 1, pixelFormat, pixelType, pixels)
                        else
                            GL.TexSubImage2D(target, level, 0, 0, dim.X, dim.Y, pixelFormat, pixelType, pixels)
                        GL.Check (sprintf "could not upload texture data for level %d" level)
                    )

                    GL.BindTexture(target, 0)
                    GL.Check "could not unbind texture"

                | TextureDimension.TextureCube ->
                    let target = getTextureTarget t
                    GL.BindTexture(target, t.Handle)
                    GL.Check "could not bind texture"

                    source.PinPBO(t.Context.PackAlignment, ImageTrafo.MirrorY, fun dim pixelType pixelFormat pixels size ->
                        if target = TextureTarget.TextureCubeMapArray then
                            GL.TexSubImage3D(target, level, 0, 0, slice, dim.X, dim.Y, 1, pixelFormat, pixelType, pixels)
                        else
                            let target = snd cubeSides.[slice % 6]
                            GL.TexSubImage2D(target, level, 0, 0, dim.X, dim.Y, pixelFormat, pixelType, pixels)
                        GL.Check (sprintf "could not upload texture data for level %d" level)
                    )

                    GL.BindTexture(target, 0)
                    GL.Check "could not unbind texture"
                | _ ->  
                    failwithf "cannot upload textures of kind: %A" t.Dimension
        )

    [<Extension>]
    static member Upload(this : Context, t : Texture, level : int, source : PixImage) =
        this.Upload(t, level, 0, source)

    [<Extension>]
    static member Download(this : Context, texture : Texture, level : int, slice : int, offset : V2i, target : PixImage) =
        using this.ResourceLock (fun _ ->
            let levelSize = texture.GetSize level
            let offset = V2i(offset.X, levelSize.Y - offset.Y - target.Size.Y) // flip y-offset

            // Multisampled texture requires resolve
            if texture.IsMultisampled then
                let resolved = this.CreateTexture2D(target.Size, 1, texture.Format, 1)
                try
                    let region = Box2i.FromMinAndSize(offset, target.Size)
                    this.Blit(texture, level, slice, region, resolved, 0, 0, region, false)
                    downloadTexture2D resolved 0 0 V2i.Zero target
                finally
                    this.Delete resolved

            // Download directly
            else
                downloadTexture texture level slice offset target
        )

    [<Extension>]
    static member Download(this : Context, texture : Texture, level : int, slice : int, target : PixImage) =
        this.Download(texture, level, slice, V2i.Zero, target)

    [<Extension>]
    static member Download(this : Context, texture : Texture, level : int, slice : int) : PixImage =
        let fmt = TextureFormat.toDownloadFormat texture.Format
        let levelSize = texture.GetSize level
        let img = PixImage.Create(fmt, int64 levelSize.X, int64 levelSize.Y)
        this.Download(texture, level, slice, img)
        img

    [<Extension>]
    static member Download(this : Context, texture : Texture, level : int) : PixImage =
        this.Download(texture, level, 0)

    [<Extension>]
    static member Download(this : Context, texture : Texture) : PixImage =
        this.Download(texture, 0, 0)

    [<Extension>]
    static member DownloadStencil(this : Context, texture : Texture,
                                  level : int, slice : int, offset : V2i, target : Matrix<int>) =
        let image =
            let img : PixImage<int> = PixImage<int>()
            img.Volume <- target.AsVolume()
            img.Format <- Col.Format.Stencil
            img

        this.Download(texture, level, slice, offset, image)

    [<Extension>]
    static member DownloadStencil(this : Context, texture : Texture, level : int, slice : int, target : Matrix<int>) =
        this.DownloadStencil(texture, level, slice, V2i.Zero, target)

    [<Extension>]
    static member DownloadDepth(this : Context, texture : Texture,
                                level : int, slice : int, offset : V2i, target : Matrix<float32>) =
        let image =
            let img : PixImage<float32> = PixImage<float32>()
            img.Volume <- target.AsVolume()
            img.Format <- Col.Format.Depth
            img

        this.Download(texture, level, slice, offset, image)

    [<Extension>]
    static member DownloadDepth(this : Context, texture : Texture, level : int, slice : int, target : Matrix<float32>) =
        this.DownloadDepth(texture, level, slice, V2i.Zero, target)

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Texture =

    let empty =
        Texture(null,0,TextureDimension.Texture2D,0,0,V3i.Zero,None,TextureFormat.Rgba8,0L, false)

    let create1D (c : Context) (size : int) (mipLevels : int) (format : TextureFormat) =
        c.CreateTexture1D(size, mipLevels, format)

    let create2D (c : Context) (size : V2i) (mipLevels : int) (format : TextureFormat) (samples : int) =
        c.CreateTexture2D(size, mipLevels, format, samples)

    let createCube (c : Context) (size : int) (mipLevels : int) (format : TextureFormat) (samples : int) =
        c.CreateTextureCube(size, mipLevels, format, samples)

    let create3D (c : Context) (size : V3i) (mipLevels : int) (format : TextureFormat)  =
        c.CreateTexture3D(size, mipLevels, format)

    let delete (tex : Texture) =
        tex.Context.Delete(tex)

    let write (data : ITexture) (tex : Texture) =
        tex.Context.Upload(tex, data)

    let read (format : PixFormat) (level : int) (tex : Texture) : PixImage[] =
        let size = tex.GetSize level

        let pi = PixImage.Create(format, int64 size.Y, int64 size.Y)
        tex.Context.Download(tex, level, 0, pi)
        [|pi|]
