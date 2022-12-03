using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine.UI;
using UnityEngine.Profiling;

namespace SoftOcclusionCulling
{
    public enum DisplayBufferType
    {
        Color,
        DepthRed,
        DepthGray
    }

    public class SoftOcclusionCullingFeature : ScriptableRendererFeature
    {
        
        
        [System.Serializable]
        public class PassSettings
        {
            public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
            public RasterizerType RasterizerType = RasterizerType.CPU;
            public LayerMask CullingLayer;
            public DisplayBufferType DisplayBuffer = DisplayBufferType.Color;
            public bool FrustumCulling;
            public bool BackFaceCulling;
            public bool BilinearSample;
            public ShaderType FragmentShaderType = ShaderType.BlinnPhong;
        }

        SoftRasterizerPass m_SoftDepthPass;
        public PassSettings passSettings = new PassSettings();

        /// <inheritdoc/>
        public override void Create()
        {
            m_SoftDepthPass = new SoftRasterizerPass(passSettings);
        }
        
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(m_SoftDepthPass);
        }
    }


    class SoftRasterizerPass : ScriptableRenderPass
    {
        private SoftOcclusionCullingFeature.PassSettings passSettings;
        private Camera camera;
        private GameObject[] rootObjs;
        private List<RenderingObject> renderingObjects = new List<RenderingObject>();
        private int screenWidth = 120;
        private int screenHeight = 67;
        
        IRasterizer _rasterizer;
        IRasterizer _lastRasterizer;
        CPURasterizer _cpuRasterizer;
        JobRasterizer _jobRasterizer;
        
        
        StatsPanel _statsPanel;
        public RawImage rawImg;
        private Light _mainLight;
        RasterizerType _lastUseUnityNativeRendering;
        
        
        NativeArray<Vector3> boundsPositionData;
        NativeArray<Vector3Int> boundstrianglesData;
        NativeArray<Vector3> lossyScale;
        NativeArray<Vector3> eulerAngles;
        NativeArray<Vector3> position;
        public NativeArray<Color> frameBuffer;
        public NativeArray<float> depthBuffer;
        public NativeArray<bool> needMoveToCullingLayer;
        public NativeArray<bool> IsOccludee;


        public SoftRasterizerPass(SoftOcclusionCullingFeature.PassSettings passSettings)
        {
            this.passSettings = passSettings;
            renderPassEvent = passSettings.renderPassEvent;
            
            int w = Mathf.FloorToInt(screenWidth);
            int h = Mathf.FloorToInt(screenHeight);
            if(_cpuRasterizer == null)
                _cpuRasterizer = new CPURasterizer(w, h, passSettings);
            if(_jobRasterizer == null)
                _jobRasterizer = new JobRasterizer(w, h, passSettings);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            Profiler.BeginSample("SoftRasterizerPass.OnCameraSetup");
            InitSetup(ref renderingData);
            _lastUseUnityNativeRendering = passSettings.RasterizerType;
            InitSocJob();
            
            Profiler.EndSample();
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var scene = renderingData.cameraData.camera.gameObject.scene;
            if (!scene.isLoaded) return;

            CommandBuffer cmd = CommandBufferPool.Get("SoftRasterizerPass");
            if (passSettings.RasterizerType != RasterizerType.Native) {
                Render();
            }

            if(_lastUseUnityNativeRendering != passSettings.RasterizerType){
                OnOffUnityRendering();                                
                _lastUseUnityNativeRendering = passSettings.RasterizerType;
            }

            if (passSettings.RasterizerType != RasterizerType.Native) {
                SOCCulling();
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
        }

        ~SoftRasterizerPass()
        {
            boundsPositionData.Dispose();
            lossyScale.Dispose();
            eulerAngles.Dispose();
            position.Dispose();
            boundstrianglesData.Dispose();
            frameBuffer.Dispose();
            depthBuffer.Dispose();
            needMoveToCullingLayer.Dispose();
            
            _cpuRasterizer.Release();
            _jobRasterizer.Release();
            if(_rasterizer != null) _rasterizer.Release();
            if(_lastRasterizer != null) _lastRasterizer.Release();
            Debug.Log("~SoftRasterizerPass");
        }
        void Render()
        {
            switch(passSettings.RasterizerType){
                case RasterizerType.CPU:
                    _rasterizer = _cpuRasterizer;
                    break;
                case RasterizerType.CPUJobs:
                    _rasterizer = _jobRasterizer;
                    break;
            }

            if (_rasterizer != _lastRasterizer && _rasterizer != null) {
                Debug.Log($"Change Rasterizer to {_rasterizer.Name}");
                _lastRasterizer = _rasterizer;
                rawImg.texture = _rasterizer.ColorTexture;
                _statsPanel.SetRasterizerType(_rasterizer.Name);  
            }
            
            var r = _rasterizer;
            if(r == null) return;
            r.Clear(BufferMask.Color | BufferMask.Depth);
            r.SetupUniforms(camera, _mainLight);

            for (int i=0; i<renderingObjects.Count; ++i)
            {
                if (renderingObjects[i].gameObject.activeInHierarchy && renderingObjects[i].Occluder) {                    
                    r.DrawObject(renderingObjects[i]);
                }
            }
            
            r.UpdateFrame();
        }
        void SOCCulling()
        {

            // foreach (var obj in renderingObjects)
            // {
            //     if (obj.gameObject.activeInHierarchy && obj.Occludee)
            //     {
            //         obj.NeedMoveToCullingLayer = true;
            //         _rasterizer.OcclusionCulling(obj);
            //         if (obj.NeedMoveToCullingLayer) {
            //             obj.gameObject.layer = 6;
            //         }
            //         else {
            //             obj.gameObject.layer = 0;
            //         }
            //     }
            // }

            if(renderingObjects.Count == 0) return;
            Profiler.BeginSample("ObjectCullingJob");
            ObjectCullingJob obJob = new ObjectCullingJob();
            obJob.boundstrianglesData = boundstrianglesData;            // 每个AABB12个三角形
            obJob.frameBuffer = frameBuffer;
            obJob.depthBuffer = depthBuffer;
            obJob.NeedMoveToCullingLayer = needMoveToCullingLayer;
            
            obJob.screenWidth = screenWidth;
            obJob.screenHeight = screenHeight;
            
            obJob.boundsPositionData = boundsPositionData;            // 每个AABB24个Position           
            obJob.lossyScale = lossyScale;
            obJob.eulerAngles = eulerAngles;
            obJob.position = position;
            
            obJob.cameraPos = camera.transform.position;
            obJob.cameraForward = camera.transform.forward;
            obJob.cameraUp = camera.transform.up;
            obJob.cameraOrthographic = camera.orthographic;
            obJob.cameraOrthographicSize = camera.orthographicSize;
            obJob.cameraFarClipPlane = camera.farClipPlane;
            obJob.cameraNearClipPlane = camera.nearClipPlane;
            obJob.cameraFieldOfView = camera.fieldOfView;
            
            obJob._matView = _jobRasterizer._matView;
            obJob._matProjection = _jobRasterizer._matProjection;
            
            obJob.IsOccludee = IsOccludee;
            
            JobHandle handle = obJob.Schedule(renderingObjects.Count, 1);
            handle.Complete();
            Profiler.EndSample();
            
            for (int i=0; i<renderingObjects.Count; ++i)
            {
                if(renderingObjects[i].Occluder)
                    continue;
                if (needMoveToCullingLayer[i] == true) {
                    renderingObjects[i].gameObject.layer = 6;
                }
                else {
                    renderingObjects[i].gameObject.layer = 0;
                }
            }
            _rasterizer.UpdateFrame();
        }
        
        void InitSetup(ref RenderingData renderingData)
        {
            var scene = renderingData.cameraData.camera.gameObject.scene;
            if (!scene.isLoaded) return;
            
            rootObjs = scene.GetRootGameObjects();
            camera = renderingData.cameraData.camera;
            renderingObjects.Clear();
            foreach(var o in rootObjs)
            {
                renderingObjects.AddRange(o.GetComponentsInChildren<RenderingObject>());
                if (rawImg == null)
                    rawImg = o.GetComponentInChildren<RawImage>(true);
                if (_statsPanel == null)
                    _statsPanel = o.GetComponentInChildren<StatsPanel>(true);
                if (_mainLight == null)
                    _mainLight = o.GetComponentInChildren<Light>(true);
            }

            if (_statsPanel != null) {
                if(_cpuRasterizer != null && passSettings.RasterizerType == RasterizerType.CPU)
                    _cpuRasterizer.StatDelegate += _statsPanel.StatDelegate;
                if(_jobRasterizer != null && passSettings.RasterizerType == RasterizerType.CPUJobs)
                    _jobRasterizer.StatDelegate += _statsPanel.StatDelegate;
            }

            if (rawImg != null)
            {
                RectTransform rect = rawImg.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(screenWidth, screenHeight);
            }

            OnOffUnityRendering();
        }

        void InitSocJob()
        {
            if(renderingObjects.Count == 0) return;
            Profiler.BeginSample("SoftRasterizerPass.InitSocJob1");
            if(!boundsPositionData.IsCreated) boundsPositionData = new NativeArray<Vector3>(renderingObjects.Count * 24,Allocator.Persistent);
            if(!lossyScale.IsCreated) lossyScale = new NativeArray<Vector3>(renderingObjects.Count,Allocator.Persistent);
            if(!eulerAngles.IsCreated) eulerAngles = new NativeArray<Vector3>(renderingObjects.Count,Allocator.Persistent);
            if(!position.IsCreated) position = new NativeArray<Vector3>(renderingObjects.Count,Allocator.Persistent);
            if(!needMoveToCullingLayer.IsCreated) needMoveToCullingLayer = new NativeArray<bool>(renderingObjects.Count,Allocator.Persistent);
            // needMoveToCullingLayer[0] = true;
            if(!boundstrianglesData.IsCreated) boundstrianglesData = new NativeArray<Vector3Int>(12,Allocator.Persistent);
            if(!IsOccludee.IsCreated) IsOccludee = new NativeArray<bool>(renderingObjects.Count,Allocator.Persistent);
            if (_jobRasterizer != null)
            {
                frameBuffer = _jobRasterizer._frameBuffer;
                depthBuffer = _jobRasterizer._depthBuffer;
            }
            Profiler.EndSample();
            
            Profiler.BeginSample("SoftRasterizerPass.InitSocJob2");
            for (int i=0; i<renderingObjects.Count; ++i)
            {
                if (renderingObjects[i].jobData != null)
                {
                    for (int j = 0; j < 24; ++j)
                        boundsPositionData[i * 24 + j] = renderingObjects[i].jobData.boundsData[j];
                    lossyScale[i] = renderingObjects[i].jobData.lossyScale;
                    eulerAngles[i] = renderingObjects[i].jobData.eulerAngles;
                    position[i] = renderingObjects[i].jobData.position;
                    boundstrianglesData = renderingObjects[i].jobData.boundstrianglesData;
                    needMoveToCullingLayer[i] = true;
                    if (renderingObjects[i].Occluder) {
                        IsOccludee[i] = false;
                    }
                    else { IsOccludee[i] = true; }
                    
                }
            }
            Profiler.EndSample();
        }
        void OnOffUnityRendering()
        {
            if(passSettings.RasterizerType == RasterizerType.Native){
                if (rawImg != null) rawImg.gameObject.SetActive(false);
                camera.cullingMask = 0xfffffff; // 除了Culling layer
                _statsPanel.SetRasterizerType("Unity Native");
            }
            else{
                if (rawImg != null) rawImg.gameObject.SetActive(true);
                camera.cullingMask = ~(1 << 6); // 0
                if(_rasterizer!=null){
                    _statsPanel.SetRasterizerType(_rasterizer.Name);
                }
            }
        }
        
    }

}