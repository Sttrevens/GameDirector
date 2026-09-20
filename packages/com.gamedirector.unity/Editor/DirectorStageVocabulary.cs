namespace GameDirector.Unity.Editor
{
    /// <summary>Names shared by the generated offline stage and the generated
    /// example film. The stage factory binds anchors under these ids and the
    /// example/inspector templates reference the same ids, so the two can never
    /// drift apart. Games building their own stages choose their own names.</summary>
    public static class DirectorStageVocabulary
    {
        public const string LeadRoleId = "lead";
        public const string WideAnchor = "wide";
        public const string CloseAnchor = "close";
        public const string SideAnchor = "side";
        public static readonly string[] Anchors = { WideAnchor, CloseAnchor, SideAnchor };
    }
}
