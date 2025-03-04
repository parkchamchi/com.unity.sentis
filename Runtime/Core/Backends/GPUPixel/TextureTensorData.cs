using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Profiling;
using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using System.Threading;
using Unity.Jobs;
using UnityEngine.Experimental.Rendering;
using static Unity.Sentis.ShaderPropertyID;

namespace Unity.Sentis
{
    /// <summary>
    /// Represents the data storage for a `Tensor` as a render texture, for backends that use GPU pixel shaders.
    ///
    /// Sentis packs the tensor data into the pixels of an ARGB float4 texture.
    ///
    /// Sentis chooses a single tensor dimension as the blocked axis, across which data is chunked in `float4` blocks.
    ///
    /// Texture dimensions don't map directly to tensor dimensions. Sentis creates the texture with
    /// dimensions large enough for the data, and pixel shaders index the data based on both the tensor and texture dimensions.
    /// </summary>
    public class TextureTensorData : ITensorData
    {
        // Example: shape: (N, C, H, W), axis: 1 (C)
        // This layout could be used conv centric ops where the most expensive data fetches are happening on the tensor channels (second dim)
        //  => C is tiled on texture.channels(rgba) to have vectorized reads to 4 values of C
        // Resulting in
        //  * C%4 = tex.channels
        //  * N, C/4, H, W = tex.pixel
        // the remaining dims are flattened which is then used as an index into the RGBA pixel of the texture considered as
        // a flattened array of float4s, the texture width is chosen as a power of two for easy indexing with bit operations
        // e.g. X[n, k, y, x] where X.shape = (N, C, H, W)
        // maps to Tex[ty, tx][tc] where Tex is RGBA with width Tex.w (power of two) and height Tex.h (chosen to be close to square texture)
        // where:
        // ti = (((n * C/4) + k/4 * H) + y * W) + x
        //                  tc = k%4
        //                     /-------\
        //                 /-------\   |
        // ty =        /-------\   |   |
        // ti / Tex.h  |   x   |   |---/
        //             |       |---/
        //             \-------/
        //              tx =
        //              ti % Tex.h
        //
        // some values of ti will thus be OOB of original texture and some values of the block GBA,
        // however the block axis and texture dimensions can be chosen to minimize the number of these

        bool m_DisposeBufferAfterUse;
        RenderTexture m_BufferAsTexture;
        int m_WidthShift;
        int m_WidthMask;

        DataType m_DataType;
        TensorShape m_Shape;
        TensorShape m_BlockedShape;
        int m_BlockAxis;
        int m_DimAxis;
        int m_DimAxisDiv4;
        int m_StrideAxis;

        /// <summary>
        /// Returns the backing texture storing the tensor data.
        /// </summary>
        public RenderTexture bufferAsTexture => m_BufferAsTexture;
        /// <summary>
        /// Returns the power in the power of two width of the backing texture.
        /// </summary>
        public int widthShift => m_WidthShift;
        /// <summary>
        /// Returns the width of the texture - 1 for efficient masking in shaders.
        /// </summary>
        public int widthMask => m_WidthMask;

        /// <summary>
        /// Returns the data type of the associated tensor.
        /// </summary>
        public DataType dataType => m_DataType;
        /// <summary>
        /// Returns the shape of the associated tensor.
        /// </summary>
        public TensorShape shape => m_Shape;
        /// <summary>
        /// Returns the shape of the tensor with the blocked axis divided by 4.
        /// </summary>
        public TensorShape blockedShape => m_BlockedShape;
        /// <summary>
        /// Returns the axis of the tensor which is blocked.
        ///
        /// It is possible to block on negative axes by considering a tensor of shape (d0, d1 ... dn) as one of shape (1, 1, .... 1, d0, d1 ... dn).
        ///
        /// Thus negative axis values do not count from the back of the shape as elsewhere.
        /// </summary>
        public int blockAxis => m_BlockAxis;
        /// <summary>
        /// The size of the blocked axis in the original tensor shape (when not blocked).
        /// </summary>
        public int dimAxis => m_DimAxis;
        /// <summary>
        /// The size of the blocked axis in the blocked tensor shape, i.e. dimAxisDiv4 = ceil(dimAxis / 4).
        /// </summary>
        public int dimAxisDiv4 => m_DimAxisDiv4;
        /// <summary>
        /// The size of the stride of the blocked axis.
        /// </summary>
        public int strideAxis => m_StrideAxis;

        static int MaxTextureSize => Mathf.Min(SystemInfo.maxTextureSize, 16384);

        static RenderTexture CreateRenderTexture(int width, int height, RenderTextureFormat renderTextureFormat)
        {
            var renderTexture = new RenderTexture(width, height, 0, renderTextureFormat);
            renderTexture.Create();
            return renderTexture;
        }

        /// <summary>
        /// Initializes and returns an instance of `TextureTensorData` with given shape and blocked axis. A `RenderTexture` is allocated to the correct size.
        /// </summary>
        /// <param name="dataType">The data type of the tensor.</param>
        /// <param name="shape">The (unblocked) shape of the tensor.</param>
        /// <param name="axis">The axis on which to block the shape.</param>
        /// <param name="clearOnInit">Whether to zero the data on allocation. The default value is `true`.</param>
        public TextureTensorData(DataType dataType, TensorShape shape, int axis, bool clearOnInit = true)
        {
            m_DataType = dataType;
            SetShape(shape, axis);
            var numPixels = m_BlockedShape.length;
            CalculateTextureDimensions(numPixels, out var newWidthShift, out var width, out var height);
            m_WidthShift = newWidthShift;
            m_WidthMask = (1 << widthShift) - 1;
            Logger.AssertIsTrue(width <= MaxTextureSize && height <= MaxTextureSize, "Tensor of shape {0} is too big to be allocated as a TextureTensorData", m_Shape);
            m_BufferAsTexture = CreateRenderTexture(width, height, dataType == DataType.Int ? RenderTextureFormat.ARGBInt : RenderTextureFormat.ARGBFloat);

            if (clearOnInit)
            {
                var previousActiveRT = RenderTexture.active;
                RenderTexture.active = m_BufferAsTexture;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = previousActiveRT;
            }

            m_DisposeBufferAfterUse = true;
        }

        internal void SetShape(TensorShape newShape, int newBlockedAxis)
        {
            m_Shape = newShape;
            m_BlockAxis = newBlockedAxis;
            m_BlockedShape = newShape;
            if (blockAxis >= 0)
            {
                m_DimAxis = newShape[newBlockedAxis];
                m_StrideAxis = newShape.Strides(newBlockedAxis);
                m_DimAxisDiv4 = ComputeHelper.IDivC(m_DimAxis, 4);
                m_BlockedShape[newBlockedAxis] = m_DimAxisDiv4;
            }
            else
            {
                m_DimAxis = 1;
                m_StrideAxis = newShape.length;
                m_DimAxisDiv4 = 1;
            }
        }

        bool IsLayoutIdentical(TensorShape newShape, int newBlockedAxis)
        {
            if (newBlockedAxis >= 0)
            {
                var newDimAxis = newShape[newBlockedAxis];
                return newShape.Strides(newBlockedAxis) == strideAxis && (newDimAxis == dimAxis || (newDimAxis % 4 == 0 && dimAxis % 4 == 0));
            }

            return newShape.length == strideAxis && dimAxis == 1;
        }

        static void CalculateTextureDimensions(int numPixels, out int widthShift, out int width, out int height)
        {
            widthShift = ComputeHelper.CalculateWidthShift(numPixels);
            width = Mathf.Min(numPixels, 1 << widthShift);
            height = ComputeHelper.IDivC(numPixels, width);
        }

        /// <inheritdoc/>
        public ITensorData Clone()
        {
            var copy = new TextureTensorData(m_DataType, m_Shape, m_BlockAxis);

            var func = new PixelFunc("Hidden/Sentis/Copy");
            if (dataType == DataType.Int)
                func.EnableKeyword("INT");
            func.SetTensor(k_TensorPropertiesX, this);
            func.Dispatch(copy);

            return copy;
        }

        /// <summary>
        /// Finalizes the `TextureTensorData`.
        /// </summary>
        ~TextureTensorData()
        {
            if (m_BufferAsTexture == null)
                return;
            if (!m_DisposeBufferAfterUse)
                return;

            D.LogWarning($"Found unreferenced, but undisposed TextureTensorData which might lead to GPU resource leak");
        }

        /// <summary>
        /// Disposes of the `TextureTensorData` and any associated memory.
        /// </summary>
        public void Dispose()
        {
            // It isn't safe to Release RT from a finalizer thread
            if (Thread.CurrentThread == CPUBackend.MainThread)
            {
                if (m_DisposeBufferAfterUse)
                {
                    // In emergency shutdown situations active RenderTexture might be the one we are trying to release
                    if (RenderTexture.active == m_BufferAsTexture)
                        RenderTexture.active = null;

                    m_BufferAsTexture.Release();
                    m_BufferAsTexture = null;
                }

                m_DisposeBufferAfterUse = false;
            }
        }

        /// <inheritdoc/>
        public void Reserve(int count)
        {
            if (count > maxCapacity)
                throw new ArgumentException("TextureTensorData buffer is too small to reserve " + count + " elements.");
        }

        /// <inheritdoc/>
        public bool IsAsyncReadbackRequestDone()
        {
            return true;
        }

        /// <inheritdoc/>
        public void AsyncReadbackRequest(Action<bool> task = null)
        {
            task?.Invoke(true);
        }

        /// <inheritdoc/>
        public void CompleteAllPendingOperations() { }

        /// <inheritdoc/>
        public void Upload<T>(NativeArray<T> data, int srcCount, int srcOffset = 0) where T : unmanaged
        {
            if (data.Length == 0)
                return;

            var numItemToCopy = shape.length;
            var numItemAvailableInData = data.Length - srcOffset;

            Assert.IsTrue(srcOffset >= 0);
            Assert.IsTrue(numItemToCopy <= numItemAvailableInData);

            var numPixels = ComputeHelper.IDivC(numItemToCopy, 4);
            CalculateTextureDimensions(numPixels, out var linearWidthShift, out var linearWidth, out var linearHeight);

            var texture = new Texture2D(linearWidth, linearHeight, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.None);

            unsafe
            {
                void* dataPtr = (byte*)data.GetUnsafeReadOnlyPtr() + sizeof(float) * srcOffset;
                var dest = texture.GetRawTextureData<float>();
                switch (dataType)
                {
                    case DataType.Float:
                        UnsafeUtility.MemCpy(dest.GetUnsafePtr(), dataPtr, sizeof(float) * srcCount);
                        break;
                    case DataType.Int:
                    {
                        var job = new GPUPixelBurstJobs.IntBytesAsFloatJob
                        {
                            src = (int*)dataPtr,
                            dest = dest
                        };
                        var jobHandle = job.Schedule(srcCount, 1024);
                        jobHandle.Complete();
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null);
                }
            }

            texture.Apply();

            var func = new PixelFunc("Hidden/Sentis/TextureTensorDataUpload");
            func.EnableKeyword(dataType == DataType.Int ? "Int" : "Float");

            func.SetTexture(k_ID_Xptr, texture);
            func.SetInt(k_TensorPropertiesX.k_ID_WidthShift, linearWidthShift);
            func.SetInt(k_TensorPropertiesX.k_ID_WidthMask, (1 << linearWidthShift) - 1);
            func.SetTensorBlockStride(k_TensorPropertiesO, this);
            func.Dispatch(this);

            UnityEngine.Object.DestroyImmediate(texture);
        }

        /// <inheritdoc/>
        public NativeArray<T> Download<T>(int dstCount, int srcOffset = 0) where T : unmanaged
        {
            var count = shape.length;

            Profiler.BeginSample("Sentis.TextureTensorData.DownloadDataFromGPU");
            Assert.IsTrue(maxCapacity >= count);

            var linearRenderTexture = bufferAsTexture;
            var numValues = shape.length;

            if (dataType != DataType.Float || strideAxis != 1 || (dimAxis % 4 != 0 && count != dimAxis))
            {
                var numPixels = ComputeHelper.IDivC(numValues, 4);
                CalculateTextureDimensions(numPixels, out var linearWidthShift, out var linearWidth, out var linearHeight);

                linearRenderTexture = CreateRenderTexture(linearWidth, linearHeight, RenderTextureFormat.ARGBFloat);

                var func = new PixelFunc("Hidden/Sentis/TextureTensorDataDownload");
                func.EnableKeyword(dataType == DataType.Int ? "Int" : "Float");
                func.SetTensor(k_TensorPropertiesX, this);
                func.SetTensorBlockStride(k_TensorPropertiesX, this);
                func.SetInt(k_TensorPropertiesO.k_ID_WidthShift, linearWidthShift);
                func.Dispatch(linearRenderTexture);
            }

            var texture = new Texture2D(linearRenderTexture.width, linearRenderTexture.height, TextureFormat.RGBAFloat, false);

            var previousActiveRT = RenderTexture.active;
            RenderTexture.active = linearRenderTexture;
            texture.ReadPixels(new Rect(0, 0, linearRenderTexture.width, linearRenderTexture.height), 0, 0);
            texture.Apply();

            var data = new NativeArray<T>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            unsafe
            {
                void* dataPtr = (byte*)data.GetUnsafeReadOnlyPtr() + srcOffset * sizeof(float);
                var src = texture.GetRawTextureData<float>();

                switch (dataType)
                {
                    case DataType.Float:
                        UnsafeUtility.MemCpy(dataPtr, src.GetUnsafePtr(), sizeof(float) * numValues);
                        break;
                    case DataType.Int:
                    {
                        var job = new GPUPixelBurstJobs.FloatBytesAsIntJob
                        {
                            src = src,
                            dest = (int*)dataPtr
                        };
                        var jobHandle = job.Schedule(numValues, 1024);
                        jobHandle.Complete();
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null);
                }
            }

            RenderTexture.active = previousActiveRT;

            Profiler.EndSample();
            return data;
        }

        /// <summary>
        /// Moves the tensor into GPU memory on the `GPUPixel` back end device.
        /// </summary>
        /// <param name="blockAxis">Which axis to block the tensor shape on.</param>
        public static TextureTensorData Pin(Tensor X, int blockAxis, bool clearOnInit = true)
        {
            var onDevice = X.tensorOnDevice;
            if (onDevice == null)
            {
                X.AttachToDevice(new TextureTensorData(X.dataType, X.shape, blockAxis, clearOnInit));
                return X.tensorOnDevice as TextureTensorData;
            }

            if (onDevice is TextureTensorData textureTensorData)
            {
                var newTextureTensorData = textureTensorData.SwitchBlockedLayout(X.shape, blockAxis);
                X.AttachToDevice(newTextureTensorData);
                return X.tensorOnDevice as TextureTensorData;
            }

            // TODO as IConvertibleToTextureTensorData
            //if (onDevice is IConvertibleToTextureTensorData asConvertible)
            //else
            X.UploadToDevice(new TextureTensorData(X.dataType, X.shape, blockAxis, clearOnInit: false)); // device is not compatible, create new array and upload

            return X.tensorOnDevice as TextureTensorData;
        }

        /// <summary>
        /// Returns a `TextureTensorData` with the same data as this but with a new layout.
        /// If the layout of the data hasn't changed this will be the same object,
        /// otherwise we need to run a shader to perform the layout switch.
        /// </summary>
        TextureTensorData SwitchBlockedLayout(TensorShape newShape, int newBlockedAxis)
        {
            if (IsLayoutIdentical(newShape, newBlockedAxis))
            {
                SetShape(newShape, newBlockedAxis);
                return this;
            }

            var textureTensorData = new TextureTensorData(m_DataType, newShape, newBlockedAxis, false);
            var func = new PixelFunc("Hidden/Sentis/LayoutSwitchBlockedAxis");
            func.EnableKeyword(dataType == DataType.Float ? "FLOAT" : "INT");
            func.SetTensor(k_TensorPropertiesX, this);
            func.SetTensorBlockStride(k_TensorPropertiesX, this);
            func.SetTensorBlockStride(k_TensorPropertiesO, textureTensorData);
            func.Dispatch(textureTensorData);
            return textureTensorData;
        }

        /// <inheritdoc/>
        public int maxCapacity => shape.length;

        /// <inheritdoc/>
        public DeviceType deviceType => DeviceType.GPU;

        /// <summary>
        /// Returns a string that represents the `TextureTensorData`.
        /// </summary>
        public override string ToString()
        {
            return $"GPU<TextureTensorData>:{shape} texture: {bufferAsTexture}";
        }
    }
} // namespace Unity.Sentis
