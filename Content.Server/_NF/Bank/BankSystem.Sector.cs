using System.Runtime.InteropServices;
using Content.Server._NF.SectorServices;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Bank.Components;
using JetBrains.Annotations;

namespace Content.Server._NF.Bank;

public sealed partial class BankSystem : SharedBankSystem
{
    [Dependency] private SectorServiceSystem _sectorService = default!;
    [Dependency] private Content.Server._Triad.Market.IMarketDataManager _market = default!; // Triad: market data

    // The interval between sector account increases, in seconds.
    private const float AccountIncreaseInterval = 10.0f;

    // Creates ledger entries for starting account balances.
    private void OnSectorInit(EntityUid entity, SectorBankComponent component, ComponentInit args)
    {
        foreach (var account in component.Accounts)
            // Triad: capture: false. This is an opening balance, not money moving. Recording it as
            // income would credit every account its whole starting float at round start and make
            // sector income look enormous in the first minute of every round.
            AddLedgerEntry(account.Key, LedgerEntryType.TickingIncome, account.Value.Balance, capture: false);
    }

    /// <summary>
    /// Attempts to remove money from a sector bank account.
    /// </summary>
    /// <param name="account">The account to be withdrawn from</param>
    /// <param name="amount">The amount of spesos to remove from the account.</param>
    /// <returns>true if the transaction was successful, false if it was not.</returns>
    [PublicAPI]
    public bool TrySectorWithdraw(SectorBankAccount account, int amount, LedgerEntryType reason, SectorBankComponent? bank = null, bool captureStandalone = true) // Triad: add captureStandalone
    {
        if (amount <= 0)
        {
            _log.Info($"TryBankWithdraw: {amount} is invalid. Sector budget withdraw attempt. Parameters: Acc: {account} Am: {amount} Rsn: {reason}");
            return false;
        }

        // Lookup sector banks
        if (bank == null && !TryComp(_sectorService.GetServiceEntity(), out bank))
        {
            _log.Info($"TryBankWithdraw: no bank component");
            return false;
        }

        if (!bank.Accounts.ContainsKey(account))
        {
            _log.Info($"TryBankWithdraw: invalid account");
            return false;
        }

        var bankAccount = CollectionsMarshal.GetValueRefOrNullRef(bank.Accounts, account);
        if (bankAccount.Balance < amount)
        {
            _log.Info($"TryBankWithdraw: account has less money {bankAccount.Balance} than requested {amount}. Sector budget withdraw attempt. Parameters: Acc: {account} Am: {amount} Rsn: {reason}");
            return false;
        }

        bankAccount.Balance -= amount;
        AddLedgerEntry(account, reason, amount, captureStandalone); // Triad: add captureStandalone
        return true;
    }

    /// <summary>
    /// Attempts to add money to a sector bank account.
    /// </summary>
    /// <param name="mobUid">The UID that the bank account is connected to, typically the player controlled mob</param>
    /// <param name="amount">The amount of spesos to remove from the bank account</param>
    /// <param name="reason">The purpose of this withdrawal</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    [PublicAPI]
    public bool TrySectorDeposit(SectorBankAccount account, int amount, LedgerEntryType reason, SectorBankComponent? bank=null, bool captureStandalone = true) // Triad: add captureStandalone
    {
        if (amount <= 0)
        {
            _log.Info($"TryBankDeposit: {amount} is invalid, Sector budget deposit attempt. Parameters: Acc: {account} Am: {amount} Rsn: {reason}");
            return false;
        }

        // Lookup sector banks
        if (bank == null && !TryComp(_sectorService.GetServiceEntity(), out bank))
        {
            _log.Info($"TryBankDeposit: no bank component");
            return false;
        }

        if (!bank.Accounts.ContainsKey(account))
        {
            _log.Info($"TryBankDeposit: invalid account");
            return false;
        }

        var bankAccount = CollectionsMarshal.GetValueRefOrNullRef(bank.Accounts, account);
        bankAccount.Balance += amount;
        AddLedgerEntry(account, reason, amount, captureStandalone); // Triad: add captureStandalone
        return true;
    }

    /// <summary>
    /// Retrieves a character's balance via its in-game entity, if it has one.
    /// </summary>
    /// <param name="ent">The UID that the bank account is connected to, typically the player controlled mob</param>
    /// <param name="balance">When successful, contains the account balance in spesos. Otherwise, set to 0.</param>
    /// <returns>true if the account was successfully queried.</returns>
    [PublicAPI]
    public bool TryGetBalance(SectorBankAccount account, out int balance)
    {
        // Lookup sector banks
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorBankComponent? bank))
        {
            _log.Info($"TryGetBalance: no bank component");
            balance = 0;
            return false;
        }

        if (!bank.Accounts.ContainsKey(account))
        {
            _log.Info($"TryGetBalance: invalid account");
            balance = 0;
            return false;
        }

        balance = bank.Accounts[account].Balance;
        return true;
    }


    private void UpdateSectorBanks(float frameTime)
    {
        if (!TryComp(_sectorService.GetServiceEntity(), out SectorBankComponent? bank))
            return;

        bank.SecondsSinceLastIncrease += frameTime;

        float secondsToCredit = 0;
        while (bank.SecondsSinceLastIncrease > AccountIncreaseInterval)
        {
            bank.SecondsSinceLastIncrease -= AccountIncreaseInterval;
            secondsToCredit += AccountIncreaseInterval;
        }

        int seconds = (int)secondsToCredit;
        if (seconds <= 0)
            return;

        foreach (var (accountId, accountInfo) in bank.Accounts)
            // Triad: captureStandalone: false. Ticking income is five accounts on a ten second
            // interval, roughly 1,800 rows an hour and about forty percent of the whole corpus, and
            // it is fully reconstructible from IncreasePerSecond. It is sampled below instead.
            TrySectorDeposit(accountId, seconds * accountInfo.IncreasePerSecond, LedgerEntryType.TickingIncome, bank, captureStandalone: false);

        // Triad: sample balances instead. Same information, a thirtieth of the rows.
        SampleSectorBalances(bank, seconds);
    }

    // Triad: begin, sector balance sampling.
    private const float AccountSampleInterval = 60.0f;
    private float _secondsSinceLastSample;

    private readonly List<(string Account, long Balance)> _sampleBuffer = new();

    private void SampleSectorBalances(SectorBankComponent bank, int elapsedSeconds)
    {
        if (!_market.Enabled)
            return;

        _secondsSinceLastSample += elapsedSeconds;
        if (_secondsSinceLastSample < AccountSampleInterval)
            return;

        _secondsSinceLastSample = 0;

        _sampleBuffer.Clear();
        foreach (var (accountId, info) in bank.Accounts)
            _sampleBuffer.Add((accountId.ToString(), info.Balance));

        _market.RecordAccountSamples(_sampleBuffer);
    }
    // Triad: end
}
