using System;
using System.Collections.Generic;
using Mono.Cecil;

public enum GroupSource
{
    CreateArchetype,
    InitializationBlock,
    Baker,
    EntityCommandBuffer
}

public enum AccessKind
{
    Unknown,
    DirectFieldUse,
    ComponentLookupByEntity,
    ComponentLookupTryGetComponent,
    EntityManagerGetComponentData,
    IJobEntityParameter,
    IfeSingleComponent,
    IfeTupleComponent,
    UncheckedRefRwComponent,
    WholeStructInitStore,
    EntityManagerWholeStructWrite,
    EcbWholeStructWrite,
    AddressTakenField
}

public enum RewriteCapability
{
    DirectFieldRedirect,
    LookupGetItemRead,
    LookupTryGetComponentRedirect,
    LookupSetItemWholeStructWrite,
    EntityManagerGetComponentDataRedirect,
    EntityManagerWholeStructWriteRedirect,
    EcbWholeStructWriteRedirect,
    TargetWholeStructWritePreserve,
    IJobEntityPeerInjection,
    IJobEntityByValuePeerMaterialization,
    IfePeerInjection,
    UncheckedRefRwPeerRedirect,
    LdfldaPeerRedirect,
    WholeStructInitRewrite
}

public class ComponentTypeInfo
{
    public TypeDefinition Type;
    public bool IsEnableable;

    public ComponentTypeInfo(TypeDefinition type, bool isEnableable)
    {
        Type = type;
        IsEnableable = isEnableable;
    }
}

public class CandidateGroup
{
    public HashSet<ComponentTypeInfo> Components;
    public GroupSource Source;

    public CandidateGroup(HashSet<ComponentTypeInfo> components, GroupSource source)
    {
        Components = components;
        Source = source;
    }
}

public class CandidateAssessment
{
    public CandidateGroup Group;
    public bool RequiresQueryRewrite;
    public Dictionary<string, HashSet<string>> RewriteSitesByComponent;

    public CandidateAssessment(
        CandidateGroup group,
        bool requiresQueryRewrite,
        Dictionary<string, HashSet<string>> rewriteSitesByComponent)
    {
        Group = group;
        RequiresQueryRewrite = requiresQueryRewrite;
        RewriteSitesByComponent = rewriteSitesByComponent;
    }
}

public class EntityShapeFact
{
    public GroupSource Source;
    public HashSet<string> ComponentFullNames;
    public string Provenance;

    public EntityShapeFact(GroupSource source, HashSet<string> componentFullNames, string provenance)
    {
        Source = source;
        ComponentFullNames = componentFullNames;
        Provenance = provenance;
    }
}

public class FieldAccessSite
{
    public string MethodFullName;
    public string FieldFullName;
    public string SourceComponentFullName;
    public AccessKind Kind;
    public string OpCodeName;
    public bool ExplicitEntityIndirection;
    public HashSet<string> RequiredEntityComponents;
    public HashSet<string> AvailableUncheckedRefRwComponents;
    public HashSet<string> AvailableUncheckedRefRoComponents;
    public HashSet<RewriteCapability> RequiredCapabilities;
    public string SiteLabel;

    public FieldAccessSite(
        string methodFullName,
        string fieldFullName,
        string sourceComponentFullName,
        AccessKind kind,
        string opCodeName,
        bool explicitEntityIndirection,
        HashSet<string> requiredEntityComponents,
        HashSet<string> availableUncheckedRefRwComponents,
        HashSet<string> availableUncheckedRefRoComponents,
        HashSet<RewriteCapability> requiredCapabilities,
        string siteLabel)
    {
        MethodFullName = methodFullName;
        FieldFullName = fieldFullName;
        SourceComponentFullName = sourceComponentFullName;
        Kind = kind;
        OpCodeName = opCodeName;
        ExplicitEntityIndirection = explicitEntityIndirection;
        RequiredEntityComponents = requiredEntityComponents;
        AvailableUncheckedRefRwComponents = availableUncheckedRefRwComponents;
        AvailableUncheckedRefRoComponents = availableUncheckedRefRoComponents;
        RequiredCapabilities = requiredCapabilities;
        SiteLabel = siteLabel;
    }
}

public class RelocationOpportunity
{
    public string SourceComponentFullName;
    public string SourceFieldFullName;
    public string TargetComponentFullName;
    public string SourceAssemblyName;
    public string TargetAssemblyName;
    public HashSet<string> AccessAssemblyNames;
    public bool SemanticAllowed;
    public bool RewriteSupportedNow;
    public HashSet<RewriteCapability> RequiredCapabilities;
    public List<string> SemanticReasons;
    public List<string> RewriteBlockers;

    public RelocationOpportunity(
        string sourceComponentFullName,
        string sourceFieldFullName,
        string targetComponentFullName,
        bool semanticAllowed,
        bool rewriteSupportedNow,
        HashSet<RewriteCapability> requiredCapabilities,
        List<string> semanticReasons,
        List<string> rewriteBlockers,
        string sourceAssemblyName = null,
        string targetAssemblyName = null,
        HashSet<string> accessAssemblyNames = null)
    {
        SourceComponentFullName = sourceComponentFullName;
        SourceFieldFullName = sourceFieldFullName;
        TargetComponentFullName = targetComponentFullName;
        SourceAssemblyName = sourceAssemblyName ?? string.Empty;
        TargetAssemblyName = targetAssemblyName ?? string.Empty;
        AccessAssemblyNames = accessAssemblyNames ?? new HashSet<string>(StringComparer.Ordinal);
        SemanticAllowed = semanticAllowed;
        RewriteSupportedNow = rewriteSupportedNow;
        RequiredCapabilities = requiredCapabilities;
        SemanticReasons = semanticReasons;
        RewriteBlockers = rewriteBlockers;
    }
}

public class CandidateAnalysisResult
{
    public List<CandidateAssessment> EligibleNow;
    public List<CandidateAssessment> EligibleWithRewrite;
    public List<EntityShapeFact> EntityShapes;
    public List<FieldAccessSite> FieldAccessSites;
    public List<RelocationOpportunity> RelocationOpportunities;

    public CandidateAnalysisResult(
        List<CandidateAssessment> eligibleNow,
        List<CandidateAssessment> eligibleWithRewrite,
        List<EntityShapeFact> entityShapes,
        List<FieldAccessSite> fieldAccessSites,
        List<RelocationOpportunity> relocationOpportunities)
    {
        EligibleNow = eligibleNow;
        EligibleWithRewrite = eligibleWithRewrite;
        EntityShapes = entityShapes;
        FieldAccessSites = fieldAccessSites;
        RelocationOpportunities = relocationOpportunities;
    }
}
