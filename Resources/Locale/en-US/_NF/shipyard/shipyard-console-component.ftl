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
shipyard-console-store-in-button = Store in #{$berth}
shipyard-console-store-no-fit-button = No berth fits
# The Store dropdown: one entry per free berth, the too-small ones greyed with the reason.
shipyard-console-store-item = #{$berth} {$class}
shipyard-console-store-too-small = too small
shipyard-console-store-takes-landing = Takes the berth the offer below lands in.
shipyard-console-lockout-title = ACCESS DENIED
shipyard-console-lockout-subtitle = BIOMETRIC MISMATCH
shipyard-console-lockout-body = This card is registered to another operator.
# The same screen for a voucher in the slot or an operator the drydock bars (TDF, TFA and the other voucher-issued roles).
shipyard-console-denied-subtitle = Enlisted personnel may not store provisioned equipment.
# One civilian ship out per account.
shipyard-console-purchase-ship-out = You already have a ship underway.
# The card for a ship the operator has out while the inserted card carries no deed.
shipyard-console-reissue-ship = · {$class} · underway
shipyard-console-reissue-button = Transfer deed
shipyard-console-reissue-tooltip = Moves this ship's deed to the inserted card and removes it from every other card.
# A store is a hand-over at a berth: the ship has to be docked to the station this console is on.
shipyard-console-store-not-docked = Dock the ship at this station first. A ship is stored from its berth, not from open space.
shipyard-console-store-not-docked-note = Not docked at this station.
# The name is drawn on its own so it can carry the weight the class does not. How long the ship
# has been out is not drawn (2026-09-12).
shipyard-console-deed-ship = · {$class}
shipyard-console-deed-ship-new = · {$class} · never stored
shipyard-console-berths-free = {$free} of {$total} berths free
# Shown while the server works. It reports its own progress by message, so the button carries a
# real percentage; the plain strings cover the gap before the first report arrives.
shipyard-console-storing-button = Storing
shipyard-console-retrieving-button = Retrieving
shipyard-console-storing-percent-button = Storing {$percent}%
shipyard-console-retrieving-percent-button = Retrieving {$percent}%
shipyard-console-berth-row = #{$berth}
shipyard-console-berth-row-class = {$class}
shipyard-console-berth-occupant = {$ship}
shipyard-console-berth-occupant-class = · {$class}
shipyard-console-berth-empty = empty
# An empty berth that an offer on the tab would fill.
shipyard-console-berth-incoming = {$ship}, if accepted
shipyard-console-berth-buy-button = Buy berth
shipyard-console-menu-rename = Rename…
shipyard-console-menu-move = Move…
shipyard-console-menu-transfer = Transfer…
shipyard-console-menu-sell = Sell…
shipyard-console-menu-no-appraisal = no appraisal
shipyard-console-menu-nowhere = nowhere to go
shipyard-console-menu-upgrade-berth = Upgrade berth
shipyard-console-menu-sell-berth = Sell berth
shipyard-console-menu-occupied = occupied
shipyard-console-transfer-accept-button = Accept
shipyard-console-transfer-decline-button = Decline
shipyard-console-transfer-cancel-button = Cancel
# The alert line: "<owner> offers <ship> · <class> · into #N", the names drawn bold around these.
shipyard-console-transfer-offers = offers
shipyard-console-transfer-into = · {$class} · into #{$berth}
shipyard-console-transfer-no-room = · {$class} · no free berth fits
shipyard-console-transfer-minutes-left = {$minutes} m left
shipyard-console-transfer-seconds-left = {$seconds} s left
shipyard-console-transfer-escrow = offered to {$name}
shipyard-console-transfer-someone = another captain
shipyard-console-transfer-picker-title = Transfer {$ship}
shipyard-console-transfer-picker-placeholder = Captain's name
shipyard-console-transfer-picker-body = {$ship} stays in berth #{$berth} and cannot be retrieved while the offer stands. The offer expires in {$minutes} minutes.
shipyard-console-transfer-picker-button = Offer
shipyard-console-transfer-picker-empty = No other captains online.
shipyard-console-transfer-picker-berths = {$count ->
    [one] 1 berth free
   *[other] {$count} berths free
}
shipyard-console-transfer-picker-no-berth = no berth fits
shipyard-console-transfer-warning = Last free berth. {$ship} would have nowhere to dock.
shipyard-console-transfer-your-ship = Your ship
shipyard-console-sell-title = Sell {$ship}
shipyard-console-sell-body = {$ship} scraps for [bold]{$price}[/bold], {$percent}% of its {$appraisal} appraisal. Berth #{$berth} is freed.
shipyard-console-sell-warning = Cannot be undone.
shipyard-console-sell-placeholder = Type {$ship} to confirm
shipyard-console-sell-button = Sell
shipyard-console-rename-title = Rename {$ship}
shipyard-console-rename-body = Applied to hull and deed on the next retrieve.
shipyard-console-rename-placeholder = Letters, digits, spaces, dashes
shipyard-console-rename-counter = {$count} / {$max}
shipyard-console-rename-button = Rename
shipyard-console-move-title = Move {$ship}
shipyard-console-move-button = Move
shipyard-console-move-item = Berth {$berth}
shipyard-console-prompt-cancel = Cancel

# Triad: the impound lot. A ship taken from the world at round end, or by an admin, sits on a card
# above the berth list until its owner reclaims it for the fee or gives it up. A locked one offers
# neither; the reason line is what the admin or the sweep wrote. Two labelled lines, reason then fee
# (2026-09-12); the appraisal share and the no-deadline note are not drawn.
shipyard-console-impound-tag = impounded
shipyard-console-impound-reason = [bold]Reason[/bold]: {$reason}
shipyard-console-impound-fee = [bold]Reclamation fee[/bold]: [bold]{$fee}[/bold]
shipyard-console-impound-fee-free = [bold]Reclamation fee[/bold]: none
shipyard-console-impound-unaffordable-note = Not enough credits: reclaiming it costs {$fee}.
shipyard-console-impound-locked-note = Neither reclaim nor abandon is available until an admin unlocks it. No deadline. Ahelp to ask about it.
shipyard-console-impound-locked-pill = LOCKED
shipyard-console-impound-into-button = Into #{$berth}
shipyard-console-impound-no-fit-button = No berth fits
shipyard-console-impound-reclaim-button = Reclaim
shipyard-console-impound-abandon-button = Abandon…
shipyard-console-impound-locked = An admin is holding that ship. Ahelp to ask about it.
shipyard-console-impound-unaffordable = Not enough funds: reclaiming it costs {$fee}.
shipyard-console-abandon-title = Abandon {$ship}
shipyard-console-abandon-body = You give up {$ship} instead of paying the release fee. You are paid nothing for it, and it does not return to a berth.
shipyard-console-abandon-warning = Cannot be undone.
shipyard-console-abandon-placeholder = Type {$ship} to confirm
shipyard-console-abandon-button = Abandon

# Triad: legacy import. Draining the old ship-save system into the drydock.
shipyard-console-import-button = Import
shipyard-console-importing-button = Importing
shipyard-console-import-row-marker = new
shipyard-console-import-row-note = · old save
shipyard-console-import-summary = { " " }· { $count } to import
shipyard-console-import-prompt-title = Import { $ship }
shipyard-console-import-prompt-body = [color=#e0e0e0]{ $ship }[/color] files into a [bold]new berth[/bold], sized to its hull and granted with the import.

# Triad: console failure reasons. A refusal at a shipyard or drydock console reaches only the
# player who pressed, via ReportConsoleError/DenyWithReason (ShipyardSystem.Triad.Feedback.cs).
# Successes are never chat lines here; the console's own state already shows them.
shipyard-console-error-vessel-unavailable = Purchase failed: that vessel is not available to you.
shipyard-console-error-vessel-load-failed = Purchase failed: the vessel's ship file could not be loaded.
shipyard-console-error-sale-loaded-from-save = Sale failed: this vessel was loaded from a saved manifest and cannot be sold.
# Triad: removed with the footer Rename. The drydock tab's rename reads drydock-error-* keys.
# shipyard-console-error-rename-empty = Rename failed: ship name cannot be empty.
# shipyard-console-error-rename-too-long = Rename failed: ship name cannot exceed {$max} characters.
# shipyard-console-error-rename-failed = Rename failed: the ship could not be renamed.

# Triad: legacy import failure reasons.
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
