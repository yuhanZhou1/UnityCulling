using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class MyDrawCube : MonoBehaviour
{
    public int instanceCount = 100000;
    public Mesh instanceMesh;
    public Material instanceMaterial;
    public int subMeshIndex = 0;

    int cachedInstanceCount = -1;
    int cachedSubMeshIndex = -1;
    ComputeBuffer argsBuffer;
    uint[] args = new uint[5] { 0, 0, 0, 0, 0 };

    public ComputeShader compute;
    ComputeBuffer localToWorldMatrixBuffer;
    ComputeBuffer cullResult;
    int kernel;
    Camera mainCamera;

    private bool Culling = true;
    
    // Start is called before the first frame update
    void Start()
    {
        kernel = compute.FindKernel("ViewPortCulling");
        mainCamera = Camera.main;
        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), 
            ComputeBufferType.IndirectArguments);

        UpdateBuffers();
    }

    // Update is called once per frame
    void Update()
    {
        if(cachedInstanceCount != instanceCount || cachedSubMeshIndex != subMeshIndex)
            UpdateBuffers();
        if (Culling)
        {
            Vector4[] planes = MyCullTool.GetFrustumPlane(mainCamera);
            
            compute.SetBuffer(kernel,"input",localToWorldMatrixBuffer);
            cullResult.SetCounterValue(0);
            compute.SetBuffer(kernel,"cullresult",cullResult);
            compute.SetInt("instanceCount",instanceCount);
            compute.SetVectorArray("planes",planes);
            compute.Dispatch(kernel,  Mathf.CeilToInt((float)instanceCount / 64), 1, 1);
            
            instanceMaterial.SetBuffer("positionBuffer", cullResult);
            //实际要渲染的数量
            ComputeBuffer.CopyCount(cullResult,argsBuffer,sizeof(uint));
        }
        else
        {
            instanceMaterial.SetBuffer("positionBuffer", localToWorldMatrixBuffer);
            args[1] = (uint)instanceCount;
            argsBuffer.SetData(args);
            // args[1] = 0;
        }

        Graphics.DrawMeshInstancedIndirect(instanceMesh,subMeshIndex,instanceMaterial,
            new Bounds(Vector3.zero, new Vector3(200.0f, 200.0f, 200.0f)),argsBuffer);
    }

    void UpdateBuffers() {
        cullResult = new ComputeBuffer(instanceCount,sizeof(float) * 16, 
            ComputeBufferType.Append);
        
        if (instanceMesh != null)
            subMeshIndex = Mathf.Clamp(subMeshIndex, 0, instanceMesh.subMeshCount - 1);
        
        if(localToWorldMatrixBuffer != null)
            localToWorldMatrixBuffer.Release();

        localToWorldMatrixBuffer = new ComputeBuffer(instanceCount, 16 * sizeof(float));
        List<Matrix4x4> localToWorldMatrixs = new List<Matrix4x4>();

        for (int i = 0; i < instanceCount; i++) {
            float angle = Random.Range(0.0f, Mathf.PI * 2.0f);
            float distance = Random.Range(20.0f, 100.0f);
            float height = Random.Range(-2.0f, 2.0f);
            float size = Random.Range(0.05f, 0.25f);
            Vector4 position = new Vector4(Mathf.Sin(angle) * distance, 
                height, Mathf.Cos(angle) * distance, size);
            localToWorldMatrixs.Add(Matrix4x4.TRS(position, 
                Quaternion.identity, new Vector3(size, size, size)));
        }
        localToWorldMatrixBuffer.SetData(localToWorldMatrixs);
        instanceMaterial.SetBuffer("positionBuffer", localToWorldMatrixBuffer);

        if (instanceMesh != null)
        {
            args[0] = (uint)instanceMesh.GetIndexCount(subMeshIndex);
            args[1] = (uint)instanceCount;
            args[2] = (uint)instanceMesh.GetIndexStart(subMeshIndex);
            args[3] = (uint)instanceMesh.GetBaseVertex(subMeshIndex);
        } else {
            args[0] = args[1] = args[2] = args[3] = 0;
        }
        argsBuffer.SetData(args);

        cachedInstanceCount = instanceCount;
        cachedSubMeshIndex = subMeshIndex;
    }

    void OnGUI() {
        GUI.Label(new Rect(265, 25, 200, 30), "Instance Count: " + instanceCount.ToString());
        instanceCount = (int)GUI.HorizontalSlider(new Rect(25, 20, 400, 30),
            (float)instanceCount, 10000.0f, 5000000.0f);
        if (GUI.Button(new Rect(25, 50, 200, 30),"<size=40>Culling</size>"))
        {
            Culling = Culling == false?true:false;
        }
        GUI.Label(new Rect(265, 50, 200, 30), "Culling: " + Culling.ToString());
    }
    
    private void OnDisable() {
        localToWorldMatrixBuffer?.Release();
        localToWorldMatrixBuffer = null;

        cullResult?.Release();
        cullResult = null;

        argsBuffer?.Release();
        argsBuffer = null;
    }
}
