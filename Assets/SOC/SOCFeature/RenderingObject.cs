using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SoftOcclusionCulling
{
    public class RenderingObject : MonoBehaviour
    {
        public Mesh mesh;
        public bool Occluder;
        public bool Occludee;
        [HideInInspector, System.NonSerialized]
        public Texture2D texture;
        [HideInInspector, System.NonSerialized]
        public CPURenderObjectData cpuData;
        [HideInInspector, System.NonSerialized]
        public JobRenderObjectData jobData;
        [HideInInspector, System.NonSerialized]
        public bool NeedMoveToCullingLayer = true;
        private void Start()
        {
            var meshFilter = GetComponent<MeshFilter>();
            if (meshFilter != null) {
                mesh = meshFilter.mesh;
            }
            
            var meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.sharedMaterial!= null) {
                texture = meshRenderer.sharedMaterial.mainTexture as Texture2D;
            }
            
            if (texture == null) {
                texture = Texture2D.whiteTexture;
            }
            
            if (mesh != null) {
                cpuData = new CPURenderObjectData(mesh);
                jobData = new JobRenderObjectData(mesh);
                jobData.lossyScale = transform.lossyScale;
                jobData.eulerAngles = transform.rotation.eulerAngles;
                jobData.position = transform.position;
            }
        }
        void OnDestroy()
        {
            cpuData.Release();
            jobData.Release();
        }

        public Matrix4x4 GetModelMatrix()
        {
            if (transform == null)
            {
                return TransformTool.GetRotZMatrix(0);
            }

            var matScale = TransformTool.GetScaleMatrix(transform.lossyScale);

            var rotation = transform.rotation.eulerAngles;
            var rotX = TransformTool.GetRotationMatrix(Vector3.right, -rotation.x);
            var rotY = TransformTool.GetRotationMatrix(Vector3.up, -rotation.y);
            var rotZ = TransformTool.GetRotationMatrix(Vector3.forward, rotation.z);
            var matRot = rotY * rotX * rotZ; // rotation apply order: z(roll), x(pitch), y(yaw) 

            var matTranslation = TransformTool.GetTranslationMatrix(transform.position);

            return matTranslation * matRot * matScale;
        }
        
    }
}