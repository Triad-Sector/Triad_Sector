using System.Text;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Bank.Components;
using Content.Server.Database; // Triad: market data
using Content.Server._Triad.Market; // Triad: market data
using Content.Shared.Preferences; // Triad: market data
using Robust.Shared.Player; // Triad: market data

namespace Content.Server._NF.Bank;

public sealed partial class BankSystem : SharedBankSystem
{
    public void CleanupLedger()
    {
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorBankComponent? ledger))
            return;
        ledger.AccountLedgerEntries.Clear();
    }

    // Adds an entry to the ledger.
    // Only positive amounts are added.
    /// <param name="capture">
    /// Triad: whether to also record this as a standalone market transaction. True for the ordinary
    /// case, where this call is the only record of the money moving. False where a caller is
    /// building a richer transaction of its own and will attach this as a split: a pallet sale fires
    /// up to eight of these, and emitting eight standalone rows would both multiply the row count
    /// and destroy the link back to the sale that produced them.
    /// </param>
    public void AddLedgerEntry(SectorBankAccount account, LedgerEntryType entryType, int amount, bool capture = true)
    {
        if (amount <= 0)
            return;
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorBankComponent? ledger))
            return;

        var tuple = (account, entryType);
        if (ledger.AccountLedgerEntries.ContainsKey(tuple))
            ledger.AccountLedgerEntries[tuple] += amount;
        else
            ledger.AccountLedgerEntries[tuple] = amount;
        RaiseLocalEvent(new SectorLedgerUpdatedEvent());

        // Triad: the in-memory ledger above is an aggregate cleared at round restart. This makes the
        // same movement durable, with a timestamp and a round, without touching any of the twenty
        // one call sites that reach here.
        if (capture)
            CaptureLedgerEntry(account, entryType, amount, isExpense: entryType >= LedgerEntryType.FirstExpense);
    }

    // Triad: begin
    private void CaptureLedgerEntry(SectorBankAccount account, LedgerEntryType entryType, int amount, bool isExpense)
    {
        if (!_market.Enabled)
            return;

        // Expenses leave the account, so they are recorded as a negative movement. Summing splits
        // for an account over a window then gives its net change rather than its turnover.
        // Minor units, like every other amount column. Sector amounts are whole spesos.
        var signed = (isExpense ? -amount : amount) * 100L;

        var record = new MarketRecord
        {
            // The ledger entry type is the real taxonomy for these, and it is carried verbatim.
            // Kind says only how the row arrived, which is what separates a ledger movement from a
            // capture site that knows who did it and what changed hands.
            Kind = MarketTransactionKind.SectorLedger,
            LedgerEntryType = entryType.ToString(),
            Rail = MarketRail.Bank,
            Gross = signed,
            Net = signed,
        };

        record.AddSplit(account.ToString(), entryType.ToString(), signed);

        _market.Record(record);
    }
    // Triad: end


    /// <summary>
    /// Triad: records a player bank movement. Called from both session overloads, which is the true
    /// chokepoint: it is where the balance actually changes and where BalanceChangedEvent is raised.
    ///
    /// <para>A caller that knows more than "money moved" passes a part-filled record and this fills
    /// in the money and the actor. A caller that passes nothing gets a minimal row rather than no
    /// row, because an unattributed movement is still a movement and its absence would make the
    /// totals wrong.</para>
    /// </summary>
    /// <param name="signedAmount">Negative for a withdrawal, positive for a deposit.</param>
    private void CaptureBankMovement(ICommonSession session, HumanoidCharacterProfile profile,
        int signedAmount, MarketRecord? capture)
    {
        if (!_market.Enabled)
            return;

        var record = capture ?? new MarketRecord { Kind = MarketTransactionKind.Unknown };

        record.ActorUserId = session.UserId;
        record.ActorCharacterName = profile.Name;
        record.Rail = MarketRail.Bank;

        // Only fill money the caller did not already state. A site that knows its gross and tax
        // has said something this method cannot work out from a balance delta.
        if (record.Gross == 0)
            record.Gross = signedAmount * 100L;
        if (record.Net == 0)
            record.Net = signedAmount * 100L;

        _market.Record(record);

        // The character name is written once per round rather than on every transaction.
        _market.RecordParticipant(session.UserId, profile.Name);
    }

    sealed class AccountInfo
    {
        public int TotalIncome;
        public int TotalExpenses;
        public List<(LedgerEntryType Type, int Value)> Income = new();
        public List<(LedgerEntryType Type, int Value)> Expenses = new();
    }

    public string GetLedgerPrintout()
    {
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorBankComponent? ledger))
            return string.Empty;

        StringBuilder builder = new();

        // Group ledger entries by account
        Dictionary<SectorBankAccount, AccountInfo> accountDict = new();
        foreach (var value in Enum.GetValues<SectorBankAccount>())
        {
            if (value == SectorBankAccount.Invalid)
                continue;
            accountDict[value] = new AccountInfo();
        }
        foreach (var (ledgerEntry, value) in ledger.AccountLedgerEntries)
        {
            if (!accountDict.ContainsKey(ledgerEntry.Account))
                continue;
            if (ledgerEntry.Type >= LedgerEntryType.FirstExpense)
            {
                accountDict[ledgerEntry.Account].Expenses.Add((ledgerEntry.Type, value));
                accountDict[ledgerEntry.Account].TotalExpenses += value;
            }
            else
            {
                accountDict[ledgerEntry.Account].Income.Add((ledgerEntry.Type, value));
                accountDict[ledgerEntry.Account].TotalIncome += value;
            }
        }

        // Build our printouts
        foreach (var (account, accountInfo) in accountDict)
        {
            builder.AppendLine(Loc.GetString("ledger-printout-account", ("account", Loc.GetString($"ledger-tab-{account}"))));
            builder.AppendLine(Loc.GetString("ledger-printout-income-header"));
            foreach (var income in accountInfo.Income)
            {
                builder.AppendLine(
                    Loc.GetString("ledger-printout-line-item",
                        ("entryType", Loc.GetString($"ledger-entry-type-{income.Type}")),
                        ("amount", BankSystemExtensions.ToSpesoString(income.Value))
                    ));
            }
            builder.AppendLine(
                Loc.GetString("ledger-printout-total-income",
                    ("amount", BankSystemExtensions.ToSpesoString(accountInfo.TotalIncome))
                ));
            builder.AppendLine();
            builder.AppendLine(Loc.GetString("ledger-printout-expense-header"));
            foreach (var expense in accountInfo.Expenses)
            {
                builder.AppendLine(
                    Loc.GetString("ledger-printout-line-item",
                        ("entryType", Loc.GetString($"ledger-entry-type-{expense.Type}")),
                        ("amount", BankSystemExtensions.ToSpesoString(expense.Value))
                    ));
            }
            builder.AppendLine(
                Loc.GetString("ledger-printout-total-expenses",
                    ("amount", BankSystemExtensions.ToSpesoString(accountInfo.TotalExpenses))
                ));
            builder.AppendLine(
                Loc.GetString("ledger-printout-balance",
                    ("amount", BankSystemExtensions.ToSpesoString(accountInfo.TotalIncome - accountInfo.TotalExpenses))
                ));
            builder.AppendLine();
        }
        return builder.ToString();
    }
}

public sealed class SectorLedgerUpdatedEvent : EntityEventArgs;
