namespace Concurenncy.LostUpdate;

/// <summary>
/// The single row the lost-update lab fights over.
/// </summary>
public sealed class BankAccount
{
    public int Id { get; init; }

    public int Amount { get; set; }
    public byte[] RowVersion { get; set; } = null!;
}
