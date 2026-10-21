using Lumora.Domain.Enums;

namespace Lumora.Domain.Entities;

/// <summary>
/// Atomare Gruppe von FileOperations. V0.1 nur für Delete-to-Recycle.
/// Move/Rename Batches kommen ab V0.2. Plan-Apply ab V0.3 (PlanId-Referenz vorbereitet).
/// </summary>
public sealed class OperationBatch
{
    public int Id { get; set; }
    public OperationType OperationType { get; set; }
    public BatchStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int? PlanId { get; set; }
    public string? Description { get; set; }
}
