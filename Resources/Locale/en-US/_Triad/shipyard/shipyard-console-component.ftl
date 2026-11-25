## UI
shipyard-console-preview = Preview
shipyard-console-header-saved-ships = Transfer ownership of saved ships to your other characters or delete them here
shipyard-console-saved = {$owner} shuttle {$vessel} stored to shipyard by {$player}.
shipyard-console-save-appraisal-label = Estimated deployment fee after tax:{" "}
shipyard-console-save-appraisal-no-ship-selected-text = Select ship
shipyard-console-tax-rate-label = Deployment tax rate:{" "}
shipyard-console-tax-rate-price-label = { TOSTRING($tax, "P0") }

## Drydock
shipyard-console-tab-purchase = Purchase
shipyard-console-tab-drydock = Drydock
shipyard-console-retrieve-button = Retrieve

shipyard-console-store-button = Store

shipyard-console-store-reclaim-button = Store reclaim
shipyard-console-store-no-fit-button = No berth fits

shipyard-console-store-item = #{$berth} {$class}
shipyard-console-store-too-small = Too small
shipyard-console-store-takes-landing = Takes the berth the offer below lands in.
shipyard-console-lockout-title = ACCESS DENIED
shipyard-console-lockout-subtitle = BIOMETRIC MISMATCH
shipyard-console-lockout-body = This card is registered to another operator.

shipyard-console-denied-subtitle = Enlisted personnel may not store provisioned equipment.
shipyard-console-purchase-ship-out = You already have a ship underway.

shipyard-console-reissue-ship = · {$class} · underway
shipyard-console-reissue-button = Transfer deed
shipyard-console-reissue-tooltip = Moves this ship's deed to the inserted card and removes it from every other card.

shipyard-console-store-not-docked = Ship must be docked to this station first before storing.
shipyard-console-store-not-docked-note = Not docked at this station.

shipyard-console-deed-ship = · {$class}
shipyard-console-deed-ship-new = · {$class} · never stored
shipyard-console-berths-free = {$free} of {$total} berths free

shipyard-console-storing-button = Storing
shipyard-console-retrieving-button = Retrieving
shipyard-console-storing-percent-button = Storing {$percent}%
shipyard-console-retrieving-percent-button = Retrieving {$percent}%
shipyard-console-berth-row = #{$berth}
shipyard-console-berth-row-class = {$class}
shipyard-console-berth-occupant = | {$ship}
shipyard-console-berth-occupant-class = · {$class}
shipyard-console-berth-empty = empty

shipyard-console-berth-incoming = {$ship}, if accepted
shipyard-console-berth-buy-button = Buy berth

shipyard-console-detail-select = Select a berth
shipyard-console-detail-select-ship = Select a ship
shipyard-console-detail-empty-title = Berth #{$berth}
shipyard-console-detail-stored = Stored
shipyard-console-detail-owned-character = [color=#999999]Owned character:[/color] [color=yellow]{$character}[/color]
shipyard-console-detail-berth-class = Berth class:
shipyard-console-detail-ship-class = Hull class:
shipyard-console-detail-appraisal = Appraisal:
shipyard-console-detail-upgrade-to = Upgrade to: {$class}
shipyard-console-detail-transfer-owner = Transfer this ship to one of your own characters.
shipyard-console-detail-delete-ship = Delete this ship without selling it. The berth itself is not sold, however.
shipyard-console-menu-rename = Rename…
shipyard-console-menu-move = Move…
shipyard-console-menu-transfer = Transfer…
shipyard-console-menu-sell = Sell…
shipyard-console-menu-no-appraisal = No appraisal
shipyard-console-menu-nowhere = Nowhere to go
shipyard-console-menu-sell-berth = Sell
shipyard-console-menu-delete-ship = Delete ship
shipyard-console-menu-transfer-owner = Transfer owner
shipyard-console-transfer-accept-button = Accept
shipyard-console-transfer-decline-button = Decline
shipyard-console-transfer-cancel-button = Cancel

shipyard-console-transfer-offers = offers
shipyard-console-transfer-into = · {$class} · into #{$berth}
shipyard-console-transfer-no-room = · {$class} · no free berth fits
shipyard-console-transfer-minutes-left = {$minutes} m left
shipyard-console-transfer-seconds-left = {$seconds} s left
shipyard-console-transfer-escrow = offered to {$name}
shipyard-console-transfer-someone = another captain
shipyard-console-transfer-picker-title = Transfer {$ship}
shipyard-console-transfer-picker-placeholder = Captain's name
shipyard-console-transfer-picker-body = {$ship} cannot be retrieved from berth #{$berth} while the offer stands. The offer will expire {$minutes} minutes.
shipyard-console-transfer-picker-button = Offer
shipyard-console-transfer-picker-empty = No other captains online.
shipyard-console-transfer-picker-berths = {$count ->
    [one] 1 berth free
   *[other] {$count} berths free
}
shipyard-console-transfer-picker-no-berth = No berth fits
shipyard-console-transfer-warning = Last free berth. {$ship} would have nowhere to dock.
shipyard-console-transfer-your-ship = Your ship
shipyard-console-sell-title = Sell {$ship}
shipyard-console-sell-body = {$ship} scraps for [bold]{$price}[/bold], {$percent}% of its {$appraisal} appraisal. Berth #{$berth} is freed.
shipyard-console-sell-warning = Cannot be undone.
shipyard-console-sell-placeholder = Type {$ship} to confirm
shipyard-console-sell-button = Sell
shipyard-console-delete-title = Delete {$ship}
shipyard-console-delete-body = Are you sure? This [color=red]PERMANENTLY[/color] deletes your ship {$ship}. Berth #{$berth} is [color=yellow]NOT[/color] deleted.
shipyard-console-delete-button = Delete
shipyard-console-rename-title = Rename {$ship}
shipyard-console-rename-body = Applied to ship on the next retrieve.
shipyard-console-rename-placeholder = Letters, digits, spaces, dashes
shipyard-console-rename-counter = {$count} / {$max}
shipyard-console-rename-button = Rename
shipyard-console-move-title = Move {$ship}
shipyard-console-move-button = Move
shipyard-console-move-item = Berth {$berth}
shipyard-console-prompt-cancel = Cancel

shipyard-console-impound-tag = impounded
shipyard-console-impound-reason = [bold]Reason[/bold]: {$reason}
shipyard-console-impound-fee = [bold]Reclamation fee[/bold]: [bold]{$fee}[/bold]
shipyard-console-impound-fee-free = [bold]Reclamation fee[/bold]: none
shipyard-console-impound-unaffordable-note = Not enough credits: reclaiming it costs {$fee}.
shipyard-console-impound-locked-note = Neither reclaim nor abandon is available until an admin unlocks it. Ahelp to ask about it.
shipyard-console-impound-locked-pill = LOCKED
shipyard-console-impound-no-fit-button = No berth fits
shipyard-console-impound-reclaim-button = Reclaim
shipyard-console-impound-abandon-button = Abandon…
shipyard-console-impound-locked = An admin is holding that ship. Ahelp to ask about it.
shipyard-console-impound-unaffordable = Not enough funds: reclaiming it costs {$fee}.
shipyard-console-abandon-title = Abandon {$ship}
shipyard-console-abandon-body = The ship {$ship} will relinquished to the authorities instead of paying the release fee. You are not compensated for this action.
shipyard-console-abandon-warning = Cannot be undone.
shipyard-console-abandon-placeholder = Type {$ship} to confirm
shipyard-console-abandon-button = Abandon

shipyard-console-import-button = Import
shipyard-console-importing-button = Importing
shipyard-console-import-row-marker = new
shipyard-console-import-row-note = · old save
shipyard-console-import-summary = { " " }· { $count } to import
shipyard-console-import-prompt-title = Import { $ship }
shipyard-console-import-prompt-body = [color=#e0e0e0]{ $ship }[/color] slots into a [bold]new berth[/bold], sized to its hull and granted with the import.

shipyard-console-error-vessel-unavailable = Purchase failed: that vessel is not available to you.
shipyard-console-error-vessel-load-failed = Purchase failed: the vessel's ship file could not be loaded.
shipyard-console-error-sale-loaded-from-save = Sale failed: this vessel was loaded from a saved manifest and cannot be sold.
shipyard-console-error-no-berths = You do not own any berths!
shipyard-console-error-no-ships = You do not have any saved ships!

shipyard-console-import-error-in-progress = Import failed: another import is already running.
shipyard-console-import-error-disabled = Import failed: legacy ship import is disabled on this server.
shipyard-console-import-error-no-account = Import failed: no player account found for this character.
shipyard-console-import-error-barred = Import failed: enlisted personnel may not store provisioned equipment.
shipyard-console-import-error-stale-offer = Import failed: that file is not on the current import list.
shipyard-console-import-error-no-character = Import failed: no character found for this session.
shipyard-console-import-error-no-session = Import failed: no active connection found for this character.
shipyard-console-import-error-empty-file = Import failed: the selected file is empty.
shipyard-console-import-error-already-imported = Import failed: this file has already been imported.
shipyard-console-import-error-budget = Import failed: you have reached the import limit for this account.
shipyard-console-import-error-load-failed = Import failed: the ship file could not be loaded. It may be corrupted.
shipyard-console-import-error-invalid-grid = Import failed: the loaded file did not produce a valid ship.
shipyard-console-import-error-already-filed = Import failed: this ship is already filed with the drydock.
shipyard-console-import-error-store-failed = Import failed: could not file the ship ({$reason}).
