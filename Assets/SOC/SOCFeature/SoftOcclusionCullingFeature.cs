using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections;
using System.Collections.Generic;
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
            m_SoftDepthPass.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
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

        
        IRasterizer _rasterizer;
        IRasterizer _lastRasterizer;
        CPURasterizer _cpuRasterizer;
        JobRasterizer _jobRasterizer;
        
        
        StatsPanel _statsPanel;
        public RawImage rawImg;
        private Light _mainLight;
        RasterizerType _lastUseUnityNativeRendering;
        public SoftRasterizerPass(SoftOcclusionCullingFeature.PassSettings passSettings)
        {
            this.passSettings = passSettings;
            renderPassEvent = passSettings.renderPassEvent;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            InitSetup(ref renderingData);
            _lastUseUnityNativeRendering = passSettings.RasterizerType;
            OnOffUnityRendering();
            InitMeshInfo();
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {

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
            // _cpuRasterizer.Release();
        }

        void Render()
        {
            switch(passSettings.RasterizerType){
                case RasterizerType.CPU:
                    _rasterizer = _cpuRasterizer;
                    break;
            }

            if (_rasterizer != _lastRasterizer) {
                Debug.Log($"Change Rasterizer to {_rasterizer.Name}");
                _lastRasterizer = _rasterizer;
                
                rawImg.texture = _rasterizer.ColorTexture;
                _statsPanel.SetRasterizerType(_rasterizer.Name);  
            }
            
            var r = _rasterizer;
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
        void InitSetup(ref RenderingData renderingData)
        {
            var scene = renderingData.cameraData.camera.gameObject.scene;
            if (scene.isLoaded)
            {
                rootObjs = scene.GetRootGameObjects();
                camera = renderingData.cameraData.camera;
            }
            
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
            
            if (rawImg != null)// && _cpuRasterizer == null)
            {
                RectTransform rect = rawImg.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(Screen.width/16, Screen.height/16);
                int w = Mathf.FloorToInt(rect.rect.width);
                int h = Mathf.FloorToInt(rect.rect.height);
                if(_cpuRasterizer == null)
                    _cpuRasterizer = new CPURasterizer(w, h, passSettings);
                if(_jobRasterizer == null)
                    _jobRasterizer = new JobRasterizer(w, h, passSettings);
            }

            if (_statsPanel != null) {
                _cpuRasterizer.StatDelegate += _statsPanel.StatDelegate;
            }
        }
        void InitMeshInfo()
        {
            // Init Mesh 
            foreach (var obj in renderingObjects)
            {
                if (obj.gameObject.activeInHierarchy)
                {
                    if (obj.mesh == null)
                    {
                        var meshFilter = obj.gameObject.GetComponent<MeshFilter>();
                        if (meshFilter != null)
                            obj.mesh = meshFilter.sharedMesh;
                    }

                    if (obj.texture == null)
                    {
                        var meshRenderer = obj.GetComponent<MeshRenderer>();
                        if (meshRenderer != null && meshRenderer.sharedMaterial != null)
                            obj.texture = meshRenderer.sharedMaterial.mainTexture as Texture2D;
                    }

                    if(obj.texture==null)
                        obj.texture = Texture2D.whiteTexture;

                    if (obj.mesh != null && obj.cpuData == null)
                    {
                        obj.cpuData = new CPURenderObjectData(obj.mesh);
                    }
                }
            }
        }
        void OnOffUnityRendering()
        {
            if(passSettings.RasterizerType == RasterizerType.Native){
                rawImg.gameObject.SetActive(false);
                camera.cullingMask = 0xfffffff; // 除了Culling layer
                _statsPanel.SetRasterizerType("Unity Native");
            }
            else{
                rawImg.gameObject.SetActive(true);
                camera.cullingMask = ~(1 << 6); // 0
                if(_rasterizer!=null){
                    _statsPanel.SetRasterizerType(_rasterizer.Name);
                }
            }
        }

        void SOCCulling()
        {
            foreach (var obj in renderingObjects)
            {
                if (obj.gameObject.activeInHierarchy && obj.Occludee)
                {
                    obj.NeedMoveToCullingLayer = true;
                    _rasterizer.DrawObject(obj);
                    if (obj.NeedMoveToCullingLayer) {
                        obj.gameObject.layer = 6;
                    }
                    else {
                        obj.gameObject.layer = 0;
                    }
                }
            }
            _rasterizer.UpdateFrame();
            
        }
    }

}