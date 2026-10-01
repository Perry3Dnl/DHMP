namespace DHMP.RawIpv6;

internal interface IDhmpPathBudgetTarget
{
    int MaximumPayloadBytes { get; }
    int CurrentMaximumPayloadBytes { get; }
    bool DynamicPathBudgetEnabled { get; }
    int DynamicAdditionalIpv6HeaderBytes { get; }

    void ApplyConfirmedPathBudget(
        DhmpIpv6PathBudget pathBudget);

    void FallBackToMinimumPathBudget();
}
