using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HizMapGenerator
{
    public class HizMapRenderFeature : ScriptableRendererFeature
    {
        [SerializeField]
        private ComputeShader _computeShader;
        
        HizMapPass m_HizMapPass;
        
        public override void Create()
        {
            if (!_computeShader) {
                Debug.LogError("missing Hiz compute shader");
                return;
            }

            if (m_HizMapPass == null) {
                m_HizMapPass = new HizMapPass(_computeShader);
            }

            m_HizMapPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            // m_HizMapPass.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        }
    
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var cameraData = renderingData.cameraData;
            if (cameraData.isSceneViewCamera || cameraData.isPreviewCamera)
                return;
            if(cameraData.camera.name == "Preview Camera"){
                return;
            }
            if(m_HizMapPass != null)
                renderer.EnqueuePass(m_HizMapPass);
        }
    }
    
    
    class HizMapPass : ScriptableRenderPass
    {
        private HizMap _hizmap;
        public HizMapPass(ComputeShader computeShader){
            _hizmap = new HizMap(computeShader);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            _hizmap.Update(context,renderingData.cameraData.camera);
        }
        
    }

}
