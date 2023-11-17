using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
namespace GPUDrivenTerrainLearn
{
    public class MinMaxHeightMapWindow : EditorWindow
    {
        [MenuItem("GPUDriven/各项贴图生成器")]
        public static void ShowWindow()
        {
            MinMaxHeightMapWindow window = GetWindow<MinMaxHeightMapWindow>("大地图各项贴图生成器");
            window.Show();
            window.OnStart();
        }
        private static ComputeShader _minMaxComputer;
        private static ComputeShader computeShader{
            get{
                if(!_minMaxComputer){
                    _minMaxComputer = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/GPUDrivenTerrain/Shader/MinMaxHeights.compute");
                }
                return _minMaxComputer;
            }
        }
        private static ComputeShader _copyShader;
        private static ComputeShader copyShader{
            get{
                if(!_copyShader){
                    _copyShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/GPUDrivenTerrain/Shader/ResolutionCopy.compute");
                }
                return _copyShader;
            }
        }
        public void OnStart()
        {
            
        }

        private Texture2D heightMap;
        private int MinMaxMapSize = 1280;
        private int PatchSize = 8;
        private Vector3 worldSize = new Vector3(10240, 2048, 10240);
        private void OnGUI()
        {
            heightMap = EditorGUILayout.ObjectField("目标高度图", heightMap, typeof(Texture2D)) as Texture2D;
            worldSize = EditorGUILayout.Vector3Field("世界大小", worldSize);
            //生成法线
            if (GUILayout.Button("生成法线"))
            {
                GenerateNormalMapFromHeightMap(heightMap,worldSize);
            }

            PatchSize = EditorGUILayout.IntField("Patch边长（米）", PatchSize);
            MinMaxMapSize = (int) worldSize.x / PatchSize;
            if (GUILayout.Button("生成最大最小高度图"))
            {
                texture = new RenderTexture(MinMaxMapSize, MinMaxMapSize, 0, heightMap.graphicsFormat);
                texture.enableRandomWrite = true;
                copyShader.SetInt("inputSize",MinMaxMapSize);
                copyShader.SetTexture(0,"inputTexture",heightMap);
                copyShader.SetTexture(0,"Result",texture);
                copyShader.Dispatch(0,MinMaxMapSize/4,MinMaxMapSize/4,1);
                // EnsureDir();
                // List<RenderTexture> textures = new List<RenderTexture>();
                new MinMaxHeightMapEditorGenerator(texture,MinMaxMapSize,heightMap).Generate();
                //GeneratePatchMinMaxHeightTexMip0(PatchSize,);
            }

            texture = EditorGUILayout.ObjectField("拷贝的高度图", texture, typeof(RenderTexture)) as RenderTexture;
        }

        private RenderTexture texture;
        #region 法线

        public static void GenerateNormalMapFromHeightMap(Texture2D heightMap,Vector3 worldSize){
            var rtdesc = new RenderTextureDescriptor(heightMap.width,heightMap.height,RenderTextureFormat.RG32);
            rtdesc.enableRandomWrite = true;
            var rt = RenderTexture.GetTemporary(rtdesc);
            ComputeShader computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/GPUDrivenTerrain/Shader/HeightToNormal.compute");
            computeShader.SetTexture(0,Shader.PropertyToID("HeightTex"),heightMap,0);
            computeShader.SetTexture(0,Shader.PropertyToID("NormalTex"),rt,0);
            uint tx,ty,tz;
            computeShader.GetKernelThreadGroupSizes(0,out tx,out ty,out tz);
            computeShader.SetVector("TexSize",new Vector4(heightMap.width,heightMap.height,0,0));
            computeShader.SetVector("WorldSize",worldSize);
            computeShader.Dispatch(0,(int)(heightMap.width  / tx),(int)(heightMap.height/ty),1);
            var req = AsyncGPUReadback.Request(rt,0,(res)=>{
                if(res.hasError){
                    Debug.LogError("error");
                }else{
                    Debug.Log("success");
                    SaveRenderTextureTo(rt,res,"Assets/GPUDrivenTerrain/Textures/TerrainNormal.png");
                }
                RenderTexture.ReleaseTemporary(rt);
            });
        }
        public static void SaveRenderTextureTo(RenderTexture renderTexture,AsyncGPUReadbackRequest request,string path){
            var tex = ConvertToTexture2D(renderTexture,TextureFormat.ARGB32);
            var bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes(path,bytes);
            AssetDatabase.Refresh();
        }
        public static Texture2D ConvertToTexture2D(RenderTexture renderTexture,TextureFormat format){
            var original = RenderTexture.active;
            RenderTexture.active = renderTexture;
            var tex = new Texture2D(renderTexture.width,renderTexture.height,format,0,false);
            tex.filterMode = renderTexture.filterMode;
            tex.ReadPixels(new Rect(0,0,tex.width,tex.height),0,0,false);
            tex.Apply(false,false);
            RenderTexture.active = original;
            return tex;
        }
        public static Texture2D ConvertToTexture2D(RenderTexture renderTexture,TextureFormat format,AsyncGPUReadbackRequest request){
            var tex = new Texture2D(renderTexture.width,renderTexture.height,format,0,false);
            tex.filterMode = renderTexture.filterMode;
            tex.SetPixelData(request.GetData<Color32>(),0);
            tex.Apply();
            return tex;
        }

        #endregion

        #region 最大最小高度图
        private string _dir;
        private void EnsureDir(){
            var heightMapPath = AssetDatabase.GetAssetPath(heightMap);
            var dir = System.IO.Path.GetDirectoryName(heightMapPath);
            var heightMapName = System.IO.Path.GetFileNameWithoutExtension(heightMapPath);
            _dir = $"{dir}/{heightMapName}";
            if(!System.IO.Directory.Exists(_dir)){
                System.IO.Directory.CreateDirectory(_dir);
            }
        }
        private void GeneratePatchMinMaxHeightTexMip0(int patchMapSize,System.Action<RenderTexture> callback){
            int kernelIndex = 0;
            var minMaxHeightTex = CreateMinMaxHeightTexture(patchMapSize);
            int groupX,groupY;
            CalcuateGroupXY(kernelIndex,patchMapSize,out groupX,out groupY);
            computeShader.SetTexture(kernelIndex,"HeightTex",heightMap);
            computeShader.SetTexture(kernelIndex,"PatchMinMaxHeightTex",minMaxHeightTex);
            computeShader.Dispatch(kernelIndex,groupX,groupY,1);
            WaitRenderTexture(minMaxHeightTex,callback);
        }
        private RenderTexture CreateMinMaxHeightTexture(int texSize){
            RenderTextureDescriptor desc = new RenderTextureDescriptor(texSize,texSize,RenderTextureFormat.RG32,0,1);
            desc.enableRandomWrite = true;
            desc.autoGenerateMips = false;
            var rt = RenderTexture.GetTemporary(desc);
            rt.filterMode = FilterMode.Point;
            rt.Create();
            return rt;
        }
        private void CalcuateGroupXY(int kernelIndex,int textureSize,out int groupX,out int groupY){
            uint threadX,threadY,threadZ;
            computeShader.GetKernelThreadGroupSizes(kernelIndex,out threadX,out threadY,out threadZ);
            groupX = (int)(textureSize / threadX);
            groupY = (int)(textureSize / threadY);
        }
        private void WaitRenderTexture(RenderTexture renderTexture,System.Action<RenderTexture> callback){
            var request = AsyncGPUReadback.Request(renderTexture,0,TextureFormat.RG32,(res)=>{
                callback(renderTexture);
            });
            TerrainEditorUtil.UpdateGPUAsyncRequest(request);    
        }
        #endregion
    }
}