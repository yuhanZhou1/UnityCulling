using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SoftOcclusionCulling
{
    public interface IRasterizer
    {
        string Name { get; }

        void Clear(BufferMask mask);

        void SetupUniforms(Camera camera, Light mainLight);

        void DrawObject(RenderingObject ro);

        Texture ColorTexture { get; }
        
        void UpdateFrame();

        void Release();

    }
}

