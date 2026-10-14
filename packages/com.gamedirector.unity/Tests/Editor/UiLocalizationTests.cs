using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GameDirector.Unity.Editor;
using NUnit.Framework;
using UnityEngine;

namespace GameDirector.Unity.Tests
{
    public class UiLocalizationTests
    {
        private static Type Catalog
        {
            get
            {
                var type = typeof(DirectorWorkbenchWindow).Assembly.GetType("GameDirector.Unity.Editor.DirectorUi");
                Assert.IsNotNull(type, "The Editor UI needs a single bilingual catalog.");
                return type;
            }
        }
        [Test]
        public void CatalogLanguagesHaveIdenticalNonemptyKeys()
        {
            var tables = (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>)Catalog.GetField("Tables").GetValue(null);
            CollectionAssert.AreEquivalent(new[] { "en", "zh-CN" }, tables.Keys);
            CollectionAssert.AreEquivalent(tables["en"].Keys, tables["zh-CN"].Keys);
            foreach (var table in tables.Values)
                foreach (var pair in table) Assert.IsFalse(string.IsNullOrWhiteSpace(pair.Value), pair.Key);
        }
        [TestCase(null, SystemLanguage.English, "en")]
        [TestCase("", SystemLanguage.French, "en")]
        [TestCase(null, SystemLanguage.Chinese, "zh-CN")]
        [TestCase(null, SystemLanguage.ChineseSimplified, "zh-CN")]
        [TestCase(null, SystemLanguage.ChineseTraditional, "zh-CN")]
        [TestCase("en", SystemLanguage.ChineseSimplified, "en")]
        [TestCase("zh-CN", SystemLanguage.English, "zh-CN")]
        [TestCase("unsupported", SystemLanguage.ChineseTraditional, "zh-CN")]
        public void ExplicitChoiceWinsOtherwiseSystemLocaleSelectsLanguage(string preference, SystemLanguage system, string expected)
        {
            Assert.AreEqual(expected, Catalog.GetMethod("SelectLanguage").Invoke(null, new object[] { preference, system }));
        }
        [Test]
        public void ManualToggleChangesAndPersistsTheRenderedLanguage()
        {
            var key = (string)Catalog.GetField("PreferenceKey").GetRawConstantValue();
            var existed = UnityEditor.EditorPrefs.HasKey(key); var before = UnityEditor.EditorPrefs.GetString(key, "");
            try
            {
                UnityEditor.EditorPrefs.SetString(key, "en");
                var text = Catalog.GetMethod("T");
                Assert.AreEqual("Start / connect Workbench", text.Invoke(null, new object[] { "startWorkbench", new object[0] }));
                Catalog.GetMethod("Toggle").Invoke(null, null);
                Assert.AreEqual("zh-CN", UnityEditor.EditorPrefs.GetString(key));
                Assert.AreEqual("\u542f\u52a8 / \u8fde\u63a5\u5de5\u4f5c\u53f0", text.Invoke(null, new object[] { "startWorkbench", new object[0] }));
                Catalog.GetMethod("Toggle").Invoke(null, null);
                Assert.AreEqual("en", UnityEditor.EditorPrefs.GetString(key));
            }
            finally { if (existed) UnityEditor.EditorPrefs.SetString(key, before); else UnityEditor.EditorPrefs.DeleteKey(key); }
        }
        [Test]
        public void EditorAndRuntimeHaveNoCjkOutsideTheCatalog()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DirectorWorkbenchWindow).Assembly);
            var cjk = new Regex("[\\u2e80-\\u9fff\\uf900-\\ufaff\\uff00-\\uffef]");
            foreach (var assembly in new[] { "Editor", "Runtime" })
                foreach (var file in Directory.GetFiles(Path.Combine(package.resolvedPath, assembly), "*.cs", SearchOption.AllDirectories))
                    if (Path.GetFileName(file) != "DirectorUi.cs") Assert.IsFalse(cjk.IsMatch(File.ReadAllText(file)), file);
        }
    }
}
