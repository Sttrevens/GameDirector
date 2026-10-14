namespace GameDirector.Client;

/// <summary>
/// MCP tool names, pinned. The [McpServerTool(Name = ...)] attributes, the
/// generated grammar's workflow section and the checked-in tools/list snapshot
/// all bind to these constants, so a rename or a split is one deliberate,
/// reviewed change instead of silent drift between surfaces.
/// </summary>
public static class McpToolNames
{
    // Director tools: drive the running game and the local take/edit pipeline.
    public const string ValidateTimeline = "gd_validate_timeline";
    public const string GetManifest = "gd_get_manifest";
    public const string PlayTimeline = "gd_play_timeline";
    public const string Stop = "gd_stop";
    public const string Status = "gd_status";
    public const string CaptureFrame = "gd_capture_frame";
    public const string Grammar = "gd_grammar";
    public const string Take = "gd_take";
    public const string Edit = "gd_edit";
    public const string DirectorGuide = "gd_director_guide";
    public const string Doctor = "gd_doctor";
    public const string PerformanceCatalog = "gd_performance_catalog";
    public const string Resume = "gd_resume";

    // Workbench (gd_studio_*) tools: managed production shared with the UI.
    // Read-only queries and mutating actions are separate tools on purpose:
    // a host can tell inspection from change by name and annotation alone.
    public const string StudioState = "gd_studio_state";
    public const string StudioProject = "gd_studio_project";
    public const string StudioValidate = "gd_studio_validate";
    public const string StudioProduce = "gd_studio_produce";
    public const string StudioJobStatus = "gd_studio_job_status";
    public const string StudioJobCancel = "gd_studio_job_cancel";
    public const string StudioJobResume = "gd_studio_job_resume";
    public const string StudioDocument = "gd_studio_document";
    public const string StudioSaveDocument = "gd_studio_save_document";
    public const string StudioDocumentReadiness = "gd_studio_document_readiness";
    public const string StudioDocumentProduce = "gd_studio_document_produce";
    public const string StudioCatalogStatus = "gd_studio_catalog_status";
    public const string StudioCatalogRead = "gd_studio_catalog_read";
    public const string StudioCatalogScaffold = "gd_studio_catalog_scaffold";
    public const string StudioCatalogImport = "gd_studio_catalog_import";
    public const string StudioMediaList = "gd_studio_media_list";
    public const string StudioMediaImport = "gd_studio_media_import";
    public const string StudioDecision = "gd_studio_decision";
    public const string StudioDecisionRecords = "gd_studio_decision_records";
    public const string StudioDecisionSettings = "gd_studio_decision_settings";
}
