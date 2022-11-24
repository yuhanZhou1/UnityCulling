using UnityEngine;
using Unity.Collections;
using UnityEngine.Profiling;
using Unity.Jobs;

namespace SoftOcclusionCulling
{
    public class JobRasterizer : IRasterizer
    {
        int _width;
        int _height;

        SoftOcclusionCullingFeature.PassSettings _passSettings;

        Matrix4x4 _matModel;
        Matrix4x4 _matView;
        Matrix4x4 _matProjection;

        NativeArray<Color> _frameBuffer;        
        NativeArray<float> _depthBuffer;        

        Color[] temp_buf;
        float[] temp_depth_buf;

        public Texture2D texture;        

        //Stats
        int _trianglesAll, _trianglesRendered;
        int _verticesAll;

        public OnRasterizerStatUpdate StatDelegate;

        //优化GC
        Vector4[] _tmpVector4s = new Vector4[3];        
        Vector3[] _tmpVector3s = new Vector3[3];
        public string Name { get=>"CPU Jobs"; }
        public Texture ColorTexture { get=>texture; }
        
        ShaderUniforms Uniforms;
        
        public float Aspect {
            get { return (float)_width / _height; }
        }

        public JobRasterizer(int w, int h, SoftOcclusionCullingFeature.PassSettings passSettings)
        {
            this._passSettings = passSettings;

            _width = w;
            _height = h;

            texture = new Texture2D(w, h);
            texture.filterMode = FilterMode.Point;

            int bufSize = w * h;

            _frameBuffer = new NativeArray<Color>(bufSize, Allocator.Persistent);
            _depthBuffer = new NativeArray<float>(bufSize, Allocator.Persistent);

            temp_buf = new Color[bufSize];
            temp_depth_buf = new float[bufSize];
            URUtils.FillArray<float>(temp_depth_buf,0);
        }

        public void Clear(BufferMask mask)
        {
            Profiler.BeginSample("JobRasterizer.Clear");


            if ((mask & BufferMask.Color) == BufferMask.Color)
            {             
                URUtils.FillArray<Color>(temp_buf, Color.black);
                _frameBuffer.CopyFrom(temp_buf);
            }
                      
            if((mask & BufferMask.Depth) == BufferMask.Depth)
            {                
                _depthBuffer.CopyFrom(temp_depth_buf);
            }                      
                       

            _trianglesAll = _trianglesRendered = 0;
            _verticesAll = 0;

            Profiler.EndSample();
        }

        public void SetupUniforms(Camera camera, Light mainLight)
        {
            var camPos = camera.transform.position;
            camPos.z *= -1;
            Uniforms.WorldSpaceCameraPos = camPos;

            var lightDir = mainLight.transform.forward;
            lightDir.z *= -1;
            Uniforms.WorldSpaceLightDir = -lightDir;
            Uniforms.LightColor = mainLight.color * mainLight.intensity;
            
            TransformTool.SetupViewProjectionMatrix(camera, Aspect, out _matView, out _matProjection);
        }

        public void DrawObject(RenderingObject ro)
        {
            Profiler.BeginSample("JobRasterizer.DrawObject");
            Mesh mesh = ro.mesh;
            
            _matModel = ro.GetModelMatrix();                      

            Matrix4x4 mvp = _matProjection * _matView * _matModel;
            if(_passSettings.FrustumCulling && URUtils.FrustumCulling(mesh.bounds, mvp)){                
                ProfileManager.EndSample();
                return;
            }

            Matrix4x4 normalMat = _matModel.inverse.transpose;

            _verticesAll += mesh.vertexCount;
            _trianglesAll += ro.cpuData.MeshTriangles.Length / 3;
            
            NativeArray<VSOutBuf> vsOutResult = new NativeArray<VSOutBuf>(mesh.vertexCount, Allocator.TempJob);
            
            VertexShadingJob vsJob = new VertexShadingJob();            
            vsJob.positionData = ro.jobData.positionData;
            vsJob.normalData = ro.jobData.normalData;
            vsJob.mvpMat = mvp;
            vsJob.modelMat = _matModel;
            vsJob.normalMat = normalMat;
            vsJob.result = vsOutResult;
            JobHandle vsHandle = vsJob.Schedule(vsOutResult.Length, 1);                        

            TriangleJob triJob = new TriangleJob();            
            triJob.trianglesData = ro.jobData.trianglesData;
            triJob.uvData = ro.jobData.uvData;
            triJob.vsOutput = vsOutResult;
            triJob.frameBuffer = _frameBuffer;
            triJob.depthBuffer = _depthBuffer;
            triJob.screenWidth = _width;
            triJob.screenHeight = _height;                                    
            triJob.TextureData = ro.texture.GetPixelData<URColor24>(0);
            triJob.TextureWidth = ro.texture.width;
            triJob.TextureHeight = ro.texture.height;
            triJob.UseBilinear = _passSettings.BilinearSample;
            triJob.fsType = _passSettings.FragmentShaderType;
            triJob.Uniforms = Uniforms;
            JobHandle triHandle = triJob.Schedule(ro.jobData.trianglesData.Length, 2, vsHandle);
            triHandle.Complete();

            vsOutResult.Dispose();
            
            Profiler.EndSample();
        }


        public void UpdateFrame()
        {
            Profiler.BeginSample("JobRasterizer.UpdateFrame");

            switch (_passSettings.DisplayBuffer)
            {
                case DisplayBufferType.Color:
                    _frameBuffer.CopyTo(temp_buf);
                    texture.SetPixels(temp_buf);
                    break;
                case DisplayBufferType.DepthRed:
                case DisplayBufferType.DepthGray:
                    for (int i = 0; i < _depthBuffer.Length; ++i)
                    {
                        //depth_buf中的值范围是[0,1]，且最近处为1，最远处为0。因此可视化后背景是黑色
                        float c = _depthBuffer[i]; 
                        if(_passSettings.DisplayBuffer == DisplayBufferType.DepthRed)
                        {
                            temp_buf[i] = new Color(c, 0, 0);
                        }
                        else
                        {
                            temp_buf[i] = new Color(c, c, c);
                        }                        
                    }
                    texture.SetPixels(temp_buf);
                    break;
            }                                
            
            texture.Apply();

            if (StatDelegate != null)
            {
                StatDelegate(_verticesAll, _trianglesAll, _trianglesRendered);
            }

            Profiler.EndSample();
        }

        public void Release()
        {
            texture = null;
            _frameBuffer.Dispose();
            _depthBuffer.Dispose();
            temp_buf = null;
            temp_depth_buf = null;
        }
    }
}

