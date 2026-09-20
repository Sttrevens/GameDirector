using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    [CreateAssetMenu(fileName = "Director Film", menuName = "GameDirector/Film")]
    public sealed class DirectorFilmAsset : ScriptableObject
    {
        [SerializeField, HideInInspector] private string filmJson = "";
        public GameObject stageModel;
        public SceneAsset stage;
        public AudioClip suppliedSound;
        public string DocumentId => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(this));
        public JObject Film
        {
            get => string.IsNullOrEmpty(filmJson) ? EmptyFilm() : JObject.Parse(filmJson);
            set => filmJson = value.ToString();
        }
        public static JObject EmptyFilm() => new JObject
        {
            ["version"] = 1, ["title"] = "New film",
            ["frameRate"] = GameDirector.Core.Dsl.FilmDefaults.FrameRate,
            ["width"] = GameDirector.Core.Dsl.FilmDefaults.Width,
            ["height"] = GameDirector.Core.Dsl.FilmDefaults.Height,
            ["scenes"] = new JArray(), ["audio"] = new JArray(), ["subtitles"] = new JArray()
        };
    }
}
