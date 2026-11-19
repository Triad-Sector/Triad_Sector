## UI
shipyard-console-invalid-vessel = Cannot purchase vessel:
shipyard-console-menu-title = Shipyard Menu
shipyard-console-menu-listing-free = Free
shipyard-console-menu-listing-voucher = Voucher
shipyard-console-docking = {$owner} shuttle {$vessel} en route.
shipyard-console-leaving = {$owner} shuttle {$vessel} sold by {$player}.
shipyard-console-docking-secret = Unregistered vessel detected entering your sector.
shipyard-console-leaving-secret = Unregistered vessel detected leaving your sector.
shipyard-commands-purchase-desc = Spawns and FTL docks a specified shuttle from a grid file.
shipyard-console-no-idcard = No ID card present.
shipyard-console-already-deeded = ID card already has a Deed.
shipyard-console-invalid-station = Not a valid station.
shipyard-console-no-bank = No bank account found.
shipyard-console-no-deed = No ship deed found.
shipyard-console-sale-reqs = Ship must be docked and all crew disembarked.
shipyard-console-sale-not-docked = Ship must be docked.
shipyard-console-sale-organic-aboard = All crew must disembark. {$name} is still aboard.
# This error message is bad, but if it happens, something awful's happened.
shipyard-console-sale-invalid-ship = Ship is invalid and cannot be sold.
# shipyard-console-sale-unknown-reason = Ship cannot be sold: {reason}
# Triad: the variable was missing its $, so the reason never printed.
shipyard-console-sale-unknown-reason = Ship cannot be sold: {$reason}
shipyard-console-deed-label = Registered Ship:
shipyard-console-appraisal-label = Shuttle Resale Value:{" "}
shipyard-console-no-voucher-redemptions = All voucher redemptions have been used.
shipyard-console-invalid-voucher-type = This voucher cannot be used at this console.
shipyard-console-denied = You cannot purchase this ship at this time.
shipyard-console-limited = There are too many active shuttles of this type, try again later!

shipyard-console-contraband-onboard = Smuggled contraband detected onboard.
shipyard-console-station-resources = Vital station resources detected onboard.
shipyard-console-dangerous-materials = Dangerous materials detected onboard.
shipyard-console-fallback-prevent-sale = YML-class bugs detected onboard. Please file a bug report when possible.

shipyard-console-menu-size-label = Size:{" "}
shipyard-console-menu-class-label = Class:{" "}
shipyard-console-menu-engine-label = Engine:{" "}

shipyard-console-purchase-available = Purchase
shipyard-console-guidebook = Manual
# Triad: removed with the Unassign button. confirm-unassign stays: Sell and the vessel rows still read it.
# shipyard-console-unassign-deed = Unassign
# shipyard-console-deed-unassigned = Deed unassigned from ID card successfully.
shipyard-console-confirm-unassign = Are you sure?
# Triad: the footer sale button for a hull issued on a voucher (its Sell label is shipyard-console-sell-button, further down)
shipyard-console-return-button = Return Vessel
shipyard-console-return-confirm = Burn voucher?
shipyard-console-return-tooltip = Hands the vessel back for nothing. The voucher is used up.
# Triad: a ghost or admin ghost pressing a shipyard console
shipyard-console-ghost-refused = Ghosts can't use shipyard consoles. Admins act on ships through drydockadmin.
# shipyard-console-unassign-cooldown = Wait {$minutes} minute(s) before unassigning another deed.

# Keep these in enum order for ease of validation.
shipyard-console-category-All = All
shipyard-console-category-Micro = Micro
shipyard-console-category-Small = Small
shipyard-console-category-Medium = Medium
shipyard-console-category-Large = Large

shipyard-console-class-All = All
shipyard-console-class-Expedition = Expedition
shipyard-console-class-Scrapyard = Scrapyard
shipyard-console-class-Salvage = Salvage
shipyard-console-class-Science = Science
shipyard-console-class-Cargo = Cargo
shipyard-console-class-Chemistry = Chemistry
shipyard-console-class-Botany = Botany
shipyard-console-class-Engineering = Engineering
shipyard-console-class-Atmospherics = Atmospherics
shipyard-console-class-Medical = Medical
shipyard-console-class-Civilian = Civilian
shipyard-console-class-Kitchen = Kitchen
# Antag
shipyard-console-class-Syndicate = Syndicate
shipyard-console-class-Pirate = PDV
# NFSD
shipyard-console-class-Capital = Capital
shipyard-console-class-Detainment = Detainment
shipyard-console-class-Detective = Detective
shipyard-console-class-Fighter = Fighter
shipyard-console-class-Patrol = Patrol
shipyard-console-class-Pursuit = Pursuit
# Mono changes start
shipyard-console-class-Corvette = Corvette
shipyard-console-class-Frigate = Frigate
shipyard-console-class-Destroyer = Destroyer
shipyard-console-class-Cruiser = Cruiser
# Mono changes end

shipyard-console-engine-All = All
shipyard-console-engine-AME = AME
shipyard-console-engine-TEG = TEG
shipyard-console-engine-Supermatter = Supermatter
shipyard-console-engine-Tesla = Tesla
shipyard-console-engine-Singularity = Singularity
shipyard-console-engine-Solar = Solar
shipyard-console-engine-RTG = RTG
shipyard-console-engine-APU = APU
shipyard-console-engine-Welding = Welding Fuel
shipyard-console-engine-Plasma = Plasma
shipyard-console-engine-Uranium = Uranium
shipyard-console-engine-Bananium = Bananium

# Mono start
shipyard-console-engine-NFR = NFR
# Mono end

# Triad: drydock tab
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
shipyard-console-berth-occupant = {$ship}
shipyard-console-berth-occupant-class = · {$class}
shipyard-console-berth-empty = empty

shipyard-console-berth-incoming = {$ship}, if accepted
shipyard-console-berth-buy-button = Buy berth

shipyard-console-detail-select = Select a berth
shipyard-console-detail-empty-title = Berth #{$berth}
shipyard-console-detail-stored = Stored
shipyard-console-detail-berth = Berth:
shipyard-console-detail-ship-class = Hull class:
shipyard-console-detail-appraisal = Appraisal:
shipyard-console-detail-upgrade-to = Upgrade to: {$class}
shipyard-console-menu-rename = Rename…
shipyard-console-menu-move = Move…
shipyard-console-menu-transfer = Transfer…
shipyard-console-menu-sell = Sell…
shipyard-console-menu-no-appraisal = No appraisal
shipyard-console-menu-nowhere = Nowhere to go
shipyard-console-menu-sell-berth = Sell berth
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
