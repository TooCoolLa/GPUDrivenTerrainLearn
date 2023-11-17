using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
namespace GPUDrivenTerrainLearn
{
    public enum TextureType : sbyte
    {
        HeightMap,
        Layer1,
        Layer1Normal,
        Layer2,
        Layer2Normal,
        Layer3,
        Layer3Normal,
        Layer4,
        Layer4Normal,
        ControlTexture,
        NoiseTexture,
        MaxValue
    }
    public struct TextureLoadWrap
    {
        public int index;
        public Dictionary<TextureType, SingleTextureLoadWrap[]> TextureCache;

        public TextureLoadWrap(int index)
        {
            this.index = index;
            TextureCache = new Dictionary<TextureType, SingleTextureLoadWrap[]>();
            for (int textureType = 0; textureType < (int)TextureType.MaxValue; textureType++)
            {
                TextureCache[(TextureType)textureType] = new SingleTextureLoadWrap[5 + 1];
            }
        }
    }

    public struct SingleTextureLoadWrap
    {
        public Texture2D Texture;
        public LoadingState State;
    }
    //TODO:贴图池子固定大小，超过多久不用就释放lod0-1的贴图,比如lod0 池子最多4个，lod1池子最多6个Terrain
    public class TextureStreamingLoader
    {
        private readonly List<int[]> HeightMapLodSize = new List<int[]>()
        {
            //HeightMap
            new int[] {1024,2560,2560,1280,640,320 },
            //L1
            new int[] {2560,2560,1280,640,320,160 },
            //L1n
            new int[] {2560,2560,1280,640,320,160 },
            //L2
            new int[] {2560,2560,1280,640,320,160 },
            //L2n
            new int[] {2560,2560,1280,640,320,160 },
            //L3
            new int[] {2560,2560,1280,640,320,160 },
            //L3n
            new int[] {2560,2560,1280,640,320,160 },
            //L4
            new int[] {2560,2560,1280,640,320,160 },
            //L4n
            new int[] {2560,2560,1280,640,320,160 },
            
        };
        private Dictionary<int, TextureLoadWrap> TerrainLoadedTexture;
        public const int PerSideTerrainCount = 20;
        
        private MonoBehaviour mono;
        public void Start(MonoBehaviour monoBehaviour,List<int> initFullLodList)
        {
            WorldTextureCache = new Dictionary<TextureType, SingleTextureRT>();
            for (int texturetype = 0; texturetype < (int)TextureType.MaxValue; texturetype++)
            {
                WorldTextureCache[(TextureType)texturetype] = new SingleTextureRT((TextureType)texturetype,HeightMapLodSize[texturetype]);
            }
            TerrainLoadedTexture = new Dictionary<int, TextureLoadWrap>();
            for (int x = 0; x < PerSideTerrainCount; x++)
            {
                for (int y = 0; y < PerSideTerrainCount; y++)
                {
                    var TerrainIndex = x + y * PerSideTerrainCount;
                    TerrainLoadedTexture[TerrainIndex] = new TextureLoadWrap(TerrainIndex);
                    //Lod5-2直接加载
                    for (int textureType = 0; textureType < (int)TextureType.MaxValue; textureType++)
                    {
                        for (uint lod =5; lod >= 2; lod--)
                        {
                            LoadTextureSync(TerrainIndex, lod, (TextureType)textureType);
                        }
                        
                    }
                }
            }
            //加载计算出来的地形块的lod0-1
            if (initFullLodList != null && initFullLodList.Count > 0)
                for (int i = 0; i < initFullLodList.Count; i++)
                {
                    var terrainId = initFullLodList[i];
                    for (uint lod = 0; lod < 2; lod++)
                    {
                        for (int textureType = 0; textureType < (int)TextureType.MaxValue; textureType++)
                        {
                            LoadTextureSync(terrainId, lod, (TextureType)textureType);
                        }
                    }
                }
            this.mono = monoBehaviour;
            
        }

        public void Update(List<TerrainBuilder.TerrainPatch> requests)
        {
            var afterSort = requests.OrderBy(x => x.Lod);
            foreach (var request in afterSort)
            {
                var index = request.Index;
                for (int textureType = 0; textureType < (int)TextureType.MaxValue; textureType++)
                {
                    TextureType type = (TextureType)textureType;
                    if (GetStateInCache(index, request.Lod, type) == LoadingState.NotLoad)
                        mono.StartCoroutine(LoadTextureAsync(index, request.Lod, type));
                }
            }
            Copy2Mipmap(afterSort);
        }
        public void LoadTextureSync(int index,uint lod,TextureType type)
        {
            Texture2D texture2D = Resources.Load<Texture2D>($"{index}{lod}{type}");
            SetTexture2DInCache(index, lod, type, texture2D);
            SetStateInCache(index, lod, type,LoadingState.DoneLoad);
        }

        private void SetTexture2DInCache(int index, uint lod, TextureType type, Texture2D texture2D)
        {
            var wrap = TerrainLoadedTexture[index].TextureCache[type][lod];
            wrap.Texture = texture2D;
            TerrainLoadedTexture[index].TextureCache[type][lod] = wrap;
        }
        private void SetStateInCache(int index, uint lod, TextureType type, LoadingState loadingState)
        {
            TerrainLoadedTexture[index].TextureCache[type][lod].State = loadingState;
        }

        private bool GetTextureFromCache(int index, uint lod, TextureType type,out Texture2D texture)
        {
            bool ret = false;
            texture = null;
            if (TerrainLoadedTexture.TryGetValue(index, out var wrap) && wrap.TextureCache.TryGetValue(type,out var wraps) && wraps[lod].State == LoadingState.DoneLoad)
            {
                ret = true;
                texture = wraps[lod].Texture;
            }
            return ret;
        }
        private LoadingState GetStateInCache(int index, uint lod, TextureType type)
        {
           return TerrainLoadedTexture[index].TextureCache[type][lod].State;
        }
        IEnumerator LoadTextureAsync(int index, uint lod,TextureType type)
        {
            var request =  Resources.LoadAsync($"{index} {lod} {type}");
            SetStateInCache(index,lod,type,LoadingState.Loading);
            yield return request;
            var  texture2D =  request.asset as Texture2D;
            SetTexture2DInCache(index,lod,type,texture2D);
            SetStateInCache(index,lod,type,LoadingState.DoneLoad);
        }

        #region 纹理处理
        /// <summary>
        /// 
        /// </summary>
        /// <param name="requests"></param>
        private void Copy2Mipmap(IEnumerable<TerrainBuilder.TerrainPatch> requests)
        {
            foreach (TerrainBuilder.TerrainPatch terrainPatch in requests)
            {
                for (int textureType = 0; textureType < (int)TextureType.MaxValue; textureType++)
                {
                    for (uint lod = 0; lod < 2; lod++)
                    {
                        if (GetTextureFromCache(terrainPatch.Index,lod ,(TextureType)textureType, out var texture))
                        {
                            
                        }
                    }
                }
            }
        }

        private Dictionary<TextureType, SingleTextureRT> WorldTextureCache;

        #endregion
    }
    public class SingleTextureRT
    {
        public TextureType TextureType;
        public RenderTexture[] RenderTexture;
        public SingleTextureRT(TextureType type,int[] lodSize)
        {
            this.TextureType = type;
            RenderTexture = new RenderTexture[lodSize.Length];
            for (int i = 0; i < lodSize.Length; i++)
            {
                var size = lodSize[i];
                RenderTextureDescriptor descriptor =
                    new RenderTextureDescriptor(size, size, RenderTextureFormat.ARGB32);
                RenderTexture[i] = new RenderTexture(descriptor);
            }
        }
    }
}