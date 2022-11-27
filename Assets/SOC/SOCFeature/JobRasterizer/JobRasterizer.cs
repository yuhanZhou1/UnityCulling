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
        NativeArray<bool> _needMoveToCullingLayer;

        Color[] temp_buf;
        float[] temp_depth_buf;
        bool[] temp_needMove_buf;

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
            
            if(_frameBuffer.IsCreated) _frameBuffer.Dispose();
            if(_depthBuffer.IsCreated) _depthBuffer.Dispose();
            if(_needMoveToCullingLayer.IsCreated) _needMoveToCullingLayer.Dispose();
            
            _frameBuffer = new NativeArray<Color>(bufSize, Allocator.Persistent);
            _depthBuffer = new NativeArray<float>(bufSize, Allocator.Persistent);
            _needMoveToCullingLayer = new NativeArray<bool>(1, Allocator.Persistent);

            temp_buf = new Color[bufSize];
            temp_depth_buf = new float[bufSize];
            temp_needMove_buf = new bool[1];
            URUtils.FillArray<float>(temp_depth_buf,0);
            URUtils.FillArray<bool>(temp_needMove_buf,true);
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
            if (ro.mesh == null || ro.jobData == null) {
                Profiler.EndSample();
                return;
            }
            Profiler.BeginSample("DrawObject.CopyFrom");
            _needMoveToCullingLayer.CopyFrom(temp_needMove_buf);
            Profiler.EndSample();
            
            Profiler.BeginSample("DrawObject.FrustumCulling");
            Mesh mesh = ro.mesh;
            
            _matModel = ro.GetModelMatrix();                      
            
            Matrix4x4 mvp = _matProjection * _matView * _matModel;
            if(_passSettings.FrustumCulling && URUtils.FrustumCulling(mesh.bounds, mvp)){                
                Profiler.EndSample();
                return;
            }
            Profiler.EndSample();
            
            Vector4[] clipAABB = URUtils.GetClipAABB(mesh.bounds, mvp);
            Vector4 minClip = clipAABB[0];
            Vector4 maxClip = clipAABB[7];
                        
            minClip.z /= minClip.w;
            maxClip.z /= maxClip.w;
            
            minClip.z = minClip.z * 0.5f + 0.5f;
            maxClip.z = maxClip.z * 0.5f + 0.5f;
            
            Matrix4x4 normalMat = _matModel.inverse.transpose;
            
            _verticesAll += mesh.vertexCount;
            _trianglesAll += ro.cpuData.MeshTriangles.Length / 3;
            
            NativeArray<VSOutBuf> vsOutResult = new NativeArray<VSOutBuf>(mesh.vertexCount, Allocator.TempJob);
            
            VertexShadingJob vsJob = new VertexShadingJob();            
            vsJob.positionData = ro.jobData.positionData;
            vsJob.mvpMat = mvp;
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
            triJob.NeedMoveToCullingLayer = _needMoveToCullingLayer;
            triJob.maxClip = maxClip;
            triJob.minClip = minClip;
            JobHandle triHandle = triJob.Schedule(ro.jobData.trianglesData.Length, 2, vsHandle);
            triHandle.Complete();
            
            Profiler.BeginSample("DrawObject.GetLayerInfo");
            if(_needMoveToCullingLayer[0] == false)
                ro.NeedMoveToCullingLayer = false;
            Profiler.EndSample();
            
            vsOutResult.Dispose();
            
            Profiler.EndSample();
        }
        
        public void OcclusionCulling(RenderingObject ro)
        {
            Profiler.BeginSample("JobRasterizer.OcclusionCulling");
            if (ro.mesh == null || ro.jobData == null) {
                Profiler.EndSample();
                return;
            }
            _needMoveToCullingLayer.CopyFrom(temp_needMove_buf);
            
            Mesh mesh = ro.mesh;
            
            _matModel = ro.GetModelMatrix();                      
            
            Matrix4x4 mvp = _matProjection * _matView * _matModel;
            if(_passSettings.FrustumCulling && URUtils.FrustumCulling(mesh.bounds, mvp)){
                Profiler.EndSample();
                return;
            }

            Profiler.BeginSample("OcclusionCulling.AABBCulling");
            var clipAABB = URUtils.GetClipAABB(mesh.bounds, mvp);
            
            //CPU方法
            Vector4 pos0 = clipAABB[0];
            Vector4 pos2 = clipAABB[2];
            Vector4 pos4 = clipAABB[4];
            Vector4 pos6 = clipAABB[6];

            if (URUtils.Clipped(pos0,pos2,pos6) && URUtils.Clipped(pos0,pos6,pos4)) {
                Profiler.EndSample();
                Profiler.EndSample();
                return;
            }
            
            pos0.x /= pos0.w; pos0.y /= pos0.w; pos0.z /= pos0.w;
            pos2.x /= pos2.w; pos2.y /= pos2.w; pos2.z /= pos2.w;
            pos4.x /= pos4.w; pos4.y /= pos4.w; pos4.z /= pos4.w;
            pos6.x /= pos6.w; pos6.y /= pos6.w; pos6.z /= pos6.w;
            
            int max_w = _width - 1;
            int max_h = _height - 1;
            pos0.x = 0.5f * max_w * (pos0.x + 1.0f);
            pos0.y = 0.5f * max_h * (pos0.y + 1.0f);                
            pos0.z = pos0.z * 0.5f + 0.5f;
            
            pos2.x = 0.5f * max_w * (pos2.x + 1.0f);
            pos2.y = 0.5f * max_h * (pos2.y + 1.0f);                
            pos2.z = pos2.z * 0.5f + 0.5f;
            
            pos4.x = 0.5f * max_w * (pos4.x + 1.0f);
            pos4.y = 0.5f * max_h * (pos4.y + 1.0f);                
            pos4.z = pos4.z * 0.5f + 0.5f;
            
            pos6.x = 0.5f * max_w * (pos6.x + 1.0f);
            pos6.y = 0.5f * max_h * (pos6.y + 1.0f);                
            pos6.z = pos6.z * 0.5f + 0.5f;

            Vector4[] pos = new Vector4[] {pos0,pos2,pos4,pos6};

            foreach (var p in pos)
            {
                int v0x = Mathf.CeilToInt(p.x);
                v0x = v0x < 0 ? 0 : v0x;
                v0x = v0x > _width ? _width : v0x;
                int v0y = Mathf.CeilToInt(p.y);
                v0y = v0y < 0 ? 0 : v0y;
                v0y = v0y > _height ? _height : v0y;
                int index = GetIndex(v0x, v0y);
                
                _frameBuffer[index] = Color.white;
                if (p.z >= _depthBuffer[index])
                {
                    _depthBuffer[index] = p.z;
                    ro.NeedMoveToCullingLayer = false;
                    Profiler.EndSample();
                    Profiler.EndSample();
                    return;
                }
            }
            Profiler.EndSample();
            
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

        public int GetIndex(int x, int y)
        {
            return y * _width + x;
        }
        
        ~JobRasterizer()
        {
            texture = null;
            if(_frameBuffer.IsCreated) _frameBuffer.Dispose();
            if(_depthBuffer.IsCreated) _depthBuffer.Dispose();
            if(_needMoveToCullingLayer.IsCreated) _needMoveToCullingLayer.Dispose();
            temp_buf = null;
            temp_depth_buf = null;
            temp_needMove_buf = null;
            Debug.Log("~JobRasterizer.Release");
        }
        public void Release()
        {
            texture = null;
            if(_frameBuffer.IsCreated) _frameBuffer.Dispose();
            if(_depthBuffer.IsCreated) _depthBuffer.Dispose();
            if(_needMoveToCullingLayer.IsCreated) _needMoveToCullingLayer.Dispose();
            temp_buf = null;
            temp_depth_buf = null;
            temp_needMove_buf = null;
        }
    }
}

