using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameDirector.Unity
{
    /// <summary>Explicit startup ownership: this scene is an offline presentation
    /// stage. A game's automatic play launcher must consult it before networking.
    /// It does not shut down or take authority from an existing game session.</summary>
    [DisallowMultipleComponent]
    public sealed class OfflinePresentationScene : MonoBehaviour
    {
        public static bool Owns(Scene scene)
        {
            if(!scene.IsValid() || !scene.isLoaded)return false;
            foreach(var root in scene.GetRootGameObjects())
                foreach(var owner in root.GetComponentsInChildren<OfflinePresentationScene>(true))
                    if(owner.enabled)return true;
            return false;
        }
    }
}
