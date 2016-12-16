﻿namespace Aardvark.Rendering.Vulkan

open System
open System.Threading
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Collections.Generic
open System.Collections.Concurrent
open Aardvark.Base

#nowarn "9"
#nowarn "51"

// =======================================================================
// Resource Definition
// =======================================================================
type Buffer =
    class
        inherit Resource<VkBuffer>
        val mutable public Memory : DevicePtr

        member x.Size = x.Memory.Size

        interface IBackendBuffer with
            member x.Handle = x.Handle :> obj
            member x.SizeInBytes = nativeint x.Memory.Size

        new(device, handle, memory) = { inherit Resource<_>(device, handle); Memory = memory }
    end

type BufferView =
    class
        inherit Resource<VkBufferView>
        val mutable public Buffer : Buffer
        val mutable public Format : VkFormat
        val mutable public Offset : uint64
        val mutable public Size : uint64

        new(device, handle, buffer, fmt, offset, size) = { inherit Resource<_>(device, handle); Buffer = buffer; Format = fmt; Offset = offset; Size = size }
    end


// =======================================================================
// Command Extensions
// =======================================================================
[<AutoOpen>]
module BufferCommands =
    type Command with
        
        // ptr to buffer
        static member Copy(src : DevicePtr, srcOffset : int64, dst : Buffer, dstOffset : int64, size : int64) =
            if size < 0L || srcOffset < 0L || srcOffset + size > src.Size || dstOffset < 0L || dstOffset + size > dst.Size then
                failf "bad copy range"

            { new Command() with
                member x.Compatible = QueueFlags.All
                member x.Enqueue cmd =
                    let mutable srcBuffer = VkBuffer.Null
                    let device = src.Memory.Heap.Device
                    let align = device.MinUniformBufferOffsetAlignment

                    let srcOffset = src.Offset + srcOffset
                    let srcBufferOffset = Alignment.prev align srcOffset
                    let srcCopyOffset = srcOffset - srcBufferOffset
                    let srcBufferSize = size + srcCopyOffset

                    let mutable srcInfo =
                        VkBufferCreateInfo(
                            VkStructureType.BufferCreateInfo, 0n,
                            VkBufferCreateFlags.None,
                            uint64 srcBufferSize, VkBufferUsageFlags.TransferSrcBit, VkSharingMode.Exclusive, 
                            0u, NativePtr.zero
                    )

                    VkRaw.vkCreateBuffer(device.Handle, &&srcInfo, NativePtr.zero, &&srcBuffer)
                        |> check "could not create temporary buffer"

                    VkRaw.vkBindBufferMemory(device.Handle, srcBuffer, src.Memory.Handle, uint64 srcBufferOffset)
                        |> check "could not bind temporary buffer memory"

                    let mutable copyInfo = VkBufferCopy(uint64 srcCopyOffset, uint64 dstOffset, uint64 size)
                    cmd.AppendCommand()
                    VkRaw.vkCmdCopyBuffer(cmd.Handle, srcBuffer, dst.Handle, 1u, &&copyInfo)

                    { new Disposable() with
                        member x.Dispose() =
                            if srcBuffer.IsValid then VkRaw.vkDestroyBuffer(device.Handle, srcBuffer, NativePtr.zero)
                    }
            }

        // buffer to ptr
        static member Copy(src : Buffer, srcOffset : int64, dst : DevicePtr, dstOffset : int64, size : int64) =
            if size < 0L || srcOffset < 0L || srcOffset + size > src.Size || dstOffset < 0L || dstOffset + size > dst.Size then
                failf "bad copy range"

            { new Command() with
                member x.Compatible = QueueFlags.All
                member x.Enqueue cmd =
                    let mutable dstBuffer = VkBuffer.Null
                    let device = src.Device
                    let align = device.MinUniformBufferOffsetAlignment

                    let dstOffset = dst.Offset + dstOffset
                    let dstBufferOffset = Alignment.prev align dstOffset
                    let dstCopyOffset = dstOffset - dstBufferOffset
                    let dstBufferSize = size + dstCopyOffset


                    let mutable dstInfo =
                        VkBufferCreateInfo(
                            VkStructureType.BufferCreateInfo, 0n,
                            VkBufferCreateFlags.None,
                            uint64 dstBufferSize, VkBufferUsageFlags.TransferDstBit, VkSharingMode.Exclusive, 
                            0u, NativePtr.zero
                    )

                    VkRaw.vkCreateBuffer(device.Handle, &&dstInfo, NativePtr.zero, &&dstBuffer)
                        |> check "could not create temporary buffer"

                    VkRaw.vkBindBufferMemory(device.Handle, dstBuffer, dst.Memory.Handle, uint64 dstBufferOffset)
                        |> check "could not bind temporary buffer memory"


                    let mutable copyInfo = VkBufferCopy(uint64 srcOffset, uint64 dstCopyOffset, uint64 size)
                    cmd.AppendCommand()
                    VkRaw.vkCmdCopyBuffer(cmd.Handle, src.Handle, dstBuffer, 1u, &&copyInfo)

                    { new Disposable() with
                        member x.Dispose() = 
                            if dstBuffer.IsValid then VkRaw.vkDestroyBuffer(device.Handle, dstBuffer, NativePtr.zero)
                    }
            }

        // buffer to buffer
        static member Copy(src : Buffer, srcOffset : int64, dst : Buffer, dstOffset : int64, size : int64) =
            if size < 0L || srcOffset < 0L || srcOffset + size > src.Size || dstOffset < 0L || dstOffset + size > dst.Size then
                failf "bad copy range"

            { new Command() with
                member x.Compatible = QueueFlags.All
                member x.Enqueue cmd =
                    let mutable copyInfo = VkBufferCopy(uint64 srcOffset, uint64 dstOffset, uint64 size)
                    cmd.AppendCommand()
                    VkRaw.vkCmdCopyBuffer(cmd.Handle, src.Handle, dst.Handle, 1u, &&copyInfo)
                    Disposable.Empty
            }


        static member inline Copy(src : DevicePtr, dst : Buffer, size : int64) = 
            Command.Copy(src, 0L, dst, 0L, size)

        static member inline Copy(src : Buffer, dst : DevicePtr, size : int64) = 
            Command.Copy(src, 0L, dst, 0L, size)

        static member inline Copy(src : Buffer, dst : Buffer, size : int64) = 
            Command.Copy(src, 0L, dst, 0L, size)

        static member inline Copy(src : DevicePtr, dst : Buffer) = 
            Command.Copy(src, 0L, dst, 0L, min src.Size dst.Size)

        static member inline Copy(src : Buffer, dst : DevicePtr) = 
            Command.Copy(src, 0L, dst, 0L, min src.Size dst.Size)

        static member inline Copy(src : Buffer, dst : Buffer) = 
            Command.Copy(src, 0L, dst, 0L, min src.Size dst.Size)


// =======================================================================
// Resource functions for Device
// =======================================================================
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Buffer =
    [<AutoOpen>]
    module private Helpers = 
        let memoryTypes (d : Device) (bits : uint32) =
            let mutable mask = 1u
            d.Memories 
            |> Seq.filter (fun m ->
                let mask = 1u <<< m.Info.index
                bits &&& mask <> 0u
               )
            |> Seq.toList

    let private emptyBuffers = ConcurrentDictionary<Device * VkBufferUsageFlags, Buffer>()

    let empty (usage : VkBufferUsageFlags) (device : Device) =
        let key = (device, usage)
        emptyBuffers.GetOrAdd(key, fun (device, usage) ->
            let mutable info =
                VkBufferCreateInfo(
                    VkStructureType.BufferCreateInfo, 0n,
                    VkBufferCreateFlags.None,
                    0UL,
                    usage,
                    device.AllSharingMode,
                    device.AllQueueFamiliesCnt,
                    device.AllQueueFamiliesPtr
                )

            let mutable handle = VkBuffer.Null
            VkRaw.vkCreateBuffer(device.Handle, &&info, NativePtr.zero, &&handle)
                |> check "could not create empty buffer"

            let mutable reqs = VkMemoryRequirements()
            VkRaw.vkGetBufferMemoryRequirements(device.Handle, handle, &&reqs)

            let ptr = device.GetMemory(reqs.memoryTypeBits).Null
            VkRaw.vkBindBufferMemory(device.Handle, handle, ptr.Memory.Handle, 0UL)
                |> check "could not bind empty buffer's memory"

            device.OnDispose.Add (fun () ->
                VkRaw.vkDestroyBuffer(device.Handle, handle, NativePtr.zero)
                emptyBuffers.TryRemove(key) |> ignore
            )   

            Buffer(device, handle, ptr)
        )


    let alloc (flags : VkBufferUsageFlags) (size : int64) (device : Device) =
        let mutable info =
            VkBufferCreateInfo(
                VkStructureType.BufferCreateInfo, 0n,
                VkBufferCreateFlags.None,
                uint64 size, 
                flags,
                VkSharingMode.Exclusive,
                0u, NativePtr.zero
            )

        let mutable handle = VkBuffer.Null
        VkRaw.vkCreateBuffer(device.Handle, &&info, NativePtr.zero, &&handle)
            |> check "could not create buffer"

        let mutable reqs = VkMemoryRequirements()
        VkRaw.vkGetBufferMemoryRequirements(device.Handle, handle, &&reqs)

        let ptr = device.Alloc(reqs, true)

        VkRaw.vkBindBufferMemory(device.Handle, handle, ptr.Memory.Handle, uint64 ptr.Offset)
            |> check "could not bind buffer-memory"


        Buffer(device, handle, ptr)

    let internal ofWriter (flags : VkBufferUsageFlags) (size : nativeint) (writer : nativeint -> unit) (device : Device) =
        let align = int64 device.MinUniformBufferOffsetAlignment

        let deviceAlignedSize = Alignment.next align (int64 size)
        let buffer = device |> alloc flags deviceAlignedSize
        let deviceMem = buffer.Memory
        
        let hostPtr = device.HostMemory.AllocTemp(align, deviceAlignedSize)
        hostPtr.Mapped (fun dst -> writer dst)

        device.eventually {
            try do! Command.Copy(hostPtr, 0L, buffer, 0L, int64 size)
            finally hostPtr.Dispose()
        }

        buffer

    let delete (buffer : Buffer) (device : Device) =
        if buffer.Handle.IsValid && buffer.Size > 0L then
            VkRaw.vkDestroyBuffer(device.Handle, buffer.Handle, NativePtr.zero)
            buffer.Handle <- VkBuffer.Null
            buffer.Memory.Dispose()

    let ofBuffer (flags : VkBufferUsageFlags) (buffer : IBuffer) (device : Device) =
        match buffer with
            | :? ArrayBuffer as ab ->
                if ab.Data.Length <> 0 then
                    let size = nativeint ab.Data.LongLength * nativeint (Marshal.SizeOf ab.ElementType)
                    let gc = GCHandle.Alloc(ab.Data, GCHandleType.Pinned)
                    try device |> ofWriter flags size (fun dst -> Marshal.Copy(gc.AddrOfPinnedObject(), dst, size))
                    finally gc.Free()
                else
                    device |> empty flags

            | :? INativeBuffer as nb ->
                if nb.SizeInBytes <> 0 then
                    let size = nb.SizeInBytes |> nativeint
                    nb.Use(fun src ->
                        device |> ofWriter flags size (fun dst -> Marshal.Copy(src, dst, size))
                    )
                else
                    device |> empty flags
                    

            | :? Buffer as b ->
                b

            | _ ->
                failf "unsupported buffer type %A" buffer


[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module BufferView =
    let create (fmt : VkFormat) (b : Buffer) (offset : uint64) (size : uint64) (device : Device) =
        if b.Size = 0L then
            BufferView(device, VkBufferView.Null, b, fmt, offset, size)
        else
            let mutable info = 
                VkBufferViewCreateInfo(
                    VkStructureType.BufferViewCreateInfo, 0n,
                    VkBufferViewCreateFlags.MinValue,
                    b.Handle, 
                    fmt,
                    offset,
                    size
                )

            let mutable handle = VkBufferView.Null
            VkRaw.vkCreateBufferView(device.Handle, &&info, NativePtr.zero, &&handle)
                |> check "could not create BufferView"

            BufferView(device, handle, b, fmt, offset, size)

    let delete (view : BufferView) (device : Device) =
        if view.Handle.IsValid then
            VkRaw.vkDestroyBufferView(device.Handle, view.Handle, NativePtr.zero)
            view.Handle <- VkBufferView.Null


// =======================================================================
// Device Extensions
// =======================================================================
[<AbstractClass; Sealed; Extension>]
type ContextBufferExtensions private() =

    [<Extension>]
    static member inline CreateBuffer(device : Device, flags : VkBufferUsageFlags, size : int64) =
        device |> Buffer.alloc flags size

    [<Extension>]
    static member inline Delete(device : Device, buffer : Buffer) =
        device |> Buffer.delete buffer

    [<Extension>]
    static member inline CreateBuffer(device : Device, flags : VkBufferUsageFlags, b : IBuffer) =
        device |> Buffer.ofBuffer flags b


    [<Extension>]
    static member inline CreateBufferView(device : Device, buffer : Buffer, format : VkFormat, offset : int64, size : int64) =
        device |> BufferView.create format buffer (uint64 offset) (uint64 size)

    [<Extension>]
    static member inline Delete(device : Device, view : BufferView) =
        device |> BufferView.delete view

[<AutoOpen>]
module ``Buffer Format Extensions`` = 
    module VkFormat =
        let ofType =
            LookupTable.lookupTable [
                typeof<float32>, VkFormat.R32Sfloat
                typeof<V2f>, VkFormat.R32g32Sfloat
                typeof<V3f>, VkFormat.R32g32b32Sfloat
                typeof<V4f>, VkFormat.R32g32b32a32Sfloat

                typeof<int>, VkFormat.R32Sint
                typeof<V2i>, VkFormat.R32g32Sint
                typeof<V3i>, VkFormat.R32g32b32Sint
                typeof<V4i>, VkFormat.R32g32b32a32Sint

                typeof<uint32>, VkFormat.R32Uint
                typeof<uint16>, VkFormat.R16Uint
                typeof<uint8>, VkFormat.R8Uint
                typeof<C4b>, VkFormat.B8g8r8a8Unorm
                typeof<C4us>, VkFormat.R16g16b16a16Unorm
                typeof<C4ui>, VkFormat.R32g32b32a32Uint
                typeof<C4f>, VkFormat.R32g32b32a32Sfloat
            ]