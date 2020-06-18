﻿namespace Aardvark.Rendering.Vulkan

open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Aardvark.Base
open Aardvark.Rendering.Vulkan
open Microsoft.FSharp.NativeInterop

type QueryPool =
    class
        val mutable public Device : Device
        val mutable public Handle : VkQueryPool
        val mutable public Count : int
        val mutable public Type : VkQueryType

        new(d,h,c,t) = { Device = d; Handle = h; Count = c; Type = t }
    end

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module QueryPool =
    let create (typ : VkQueryType) (flags : VkQueryPipelineStatisticFlags) (cnt : int) (device : Device) =
        native {
            let! pInfo =
                VkQueryPoolCreateInfo(
                    VkQueryPoolCreateFlags.MinValue,
                    typ, uint32 cnt, flags
                )

            let! pHandle = VkQueryPool.Null
            VkRaw.vkCreateQueryPool(device.Handle, pInfo, NativePtr.zero, pHandle)
                |> check "could not create query pool"

            return QueryPool(device, !!pHandle, cnt, typ)
        }

    let delete (pool : QueryPool) =
        VkRaw.vkDestroyQueryPool(pool.Device.Handle, pool.Handle, NativePtr.zero)

    let reset (pool : QueryPool) =
        VkRaw.vkResetQueryPool(pool.Device.Handle, pool.Handle, 0u, uint32 pool.Count)

    let private getResults (valuesPerQuery : int) (flags : VkQueryResultFlags) (pool : QueryPool) =
        let bufferLength = pool.Count * valuesPerQuery
        let bufferSizeInBytes = bufferLength * sizeof<uint64>
        let bufferStride = valuesPerQuery * sizeof<uint64>

        let data : uint64[] = Array.zeroCreate bufferLength
        let gc = GCHandle.Alloc(data, GCHandleType.Pinned)

        try
            let result =
                VkRaw.vkGetQueryPoolResults(
                    pool.Device.Handle, pool.Handle, 0u, uint32 pool.Count,
                    uint64 bufferSizeInBytes, gc.AddrOfPinnedObject(),
                    uint64 bufferStride,
                    flags ||| VkQueryResultFlags.D64Bit
                )

            match result with
            | VkResult.VkSuccess -> Some data
            | VkResult.VkNotReady -> None
            | _ -> result |> check "failed to get query results" |> unbox

        finally
            gc.Free()

    let tryGetValues (valuesPerQuery : int) (pool : QueryPool) =
        pool |> getResults valuesPerQuery VkQueryResultFlags.None

    let tryGet (pool : QueryPool) =
        pool |> tryGetValues 1

    let getValues (valuesPerQuery : int) (pool : QueryPool) =
        let flags = VkQueryResultFlags.WaitBit
        pool |> getResults valuesPerQuery flags |> Option.get

    let get (pool : QueryPool) =
        pool |> getValues 1

[<AutoOpen>]
module QueryCommandExtensions =
    type Command with
        static member Reset(pool : QueryPool) =
            { new Command() with
                member x.Compatible = QueueFlags.All
                member x.Enqueue(cmd) =
                    cmd.AppendCommand()
                    VkRaw.vkCmdResetQueryPool(cmd.Handle, pool.Handle, 0u, uint32 pool.Count)

                    Disposable.Empty
            }
        static member BeginQuery(pool : QueryPool, index : int, flags : VkQueryControlFlags) =
            { new Command() with
                member x.Compatible = QueueFlags.All
                member x.Enqueue(cmd) =
                    cmd.AppendCommand()
                    VkRaw.vkCmdBeginQuery(cmd.Handle, pool.Handle, uint32 index, flags)

                    Disposable.Empty
            }
        static member EndQuery(pool : QueryPool, index : int) =
            { new Command() with
                member x.Compatible = QueueFlags.All
                member x.Enqueue(cmd) =
                    cmd.AppendCommand()
                    VkRaw.vkCmdEndQuery(cmd.Handle, pool.Handle, uint32 index)

                    Disposable.Empty
            }

        static member CopyQueryResults(pool : QueryPool, target : Buffer) =
            { new Command() with
                member x.Compatible = QueueFlags.All
                member x.Enqueue(cmd) =
                    cmd.AppendCommand()
                    VkRaw.vkCmdCopyQueryPoolResults(cmd.Handle, pool.Handle, 0u, uint32 pool.Count, target.Handle, 0UL, 8UL, VkQueryResultFlags.D64Bit ||| VkQueryResultFlags.WaitBit ||| VkQueryResultFlags.PartialBit)

                    Disposable.Empty
            }

        static member WriteTimestamp(pool : QueryPool, pipelineFlags : VkPipelineStageFlags, index : int) =
            { new Command() with
                member x.Compatible = QueueFlags.All
                member x.Enqueue(cmd) =
                    cmd.AppendCommand()
                    VkRaw.vkCmdWriteTimestamp(cmd.Handle, pipelineFlags, pool.Handle, uint32 index)

                    Disposable.Empty
            }

[<AbstractClass; Sealed; Extension>]
type DeviceQueryPoolExtensions private() =
    [<Extension>]
    static member inline CreateQueryPool(device : Device, count : int) =
        device |> QueryPool.create VkQueryType.Timestamp VkQueryPipelineStatisticFlags.None count

    [<Extension>]
    static member inline Delete(device : Device, pool : QueryPool) =
        pool |> QueryPool.delete

    [<Extension>]
    static member inline GetResults(device : Device, pool : QueryPool) =
        pool |> QueryPool.get
