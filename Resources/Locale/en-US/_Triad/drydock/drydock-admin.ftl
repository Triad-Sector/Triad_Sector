# Triad: the drydock admin panel (drydockadmin). One key per control; the tooltips are the only
# tooltips in the drydock, the player tab has none. A tooltip says what the control does; a greyed
# verb's tooltip is the bare reason.

drydock-admin-title = Drydock Administration
drydock-admin-flavor-left = SKR-OS Drydock Administration
# The button on the Admin menu's Admin tab that opens the panel.
drydock-admin-button = Drydock

drydock-admin-search-placeholder = Character, account or ship name, or callsign
drydock-admin-search-tooltip = Enter to search.

drydock-admin-chip-All = All
drydock-admin-chip-Stored = Stored
drydock-admin-chip-CheckedOut = Out
drydock-admin-chip-InEscrow = Escrow
drydock-admin-chip-Impounded = Impounded
drydock-admin-chip-Sold = Sold
drydock-admin-chip-Abandoned = Abandoned
drydock-admin-chip-Destroyed = Destroyed
drydock-admin-chip-Stranded = Stranded
drydock-admin-chip-Stranded-tooltip = Out in a round that has ended.

drydock-admin-count = { $total ->
    [one] 1 match
   *[other] { $total } matches
}
drydock-admin-page = Page {$page} of {$pages}
drydock-admin-prev = Prev
drydock-admin-next = Next

# A list card: the callsign and name, then one labelled line each for class, owner and status. The
# status is a comma list: the state, the berth when it holds one, then the state's own figure.
drydock-admin-card-title = {$callsign} | {$name}
drydock-admin-card-class = Class:
drydock-admin-card-owner = Owner:
drydock-admin-card-status = Status:
drydock-admin-status-berth = Berth {$berth}
drydock-admin-status-round = Round {$round}
drydock-admin-status-expires = Expires in {$minutes}m
drydock-admin-status-expired = Expired
drydock-admin-status-locked = Locked

drydock-admin-no-selection = Select a ship.
drydock-admin-live = Live this round

drydock-admin-impound = Impound…
drydock-admin-impound-tooltip = Moves the ship to the impound lot.
drydock-admin-release = Release
drydock-admin-release-tooltip = Releases the ship from impound. No fee.
drydock-admin-restore-to = Restore to…
drydock-admin-restore-to-tooltip = Restores the ship to a chosen berth.
drydock-admin-restore-from-sale = Restore from sale…
drydock-admin-restore-from-sale-tooltip = Reverses the sale and restores the ship.
drydock-admin-cancel-offer = Cancel offer
drydock-admin-cancel-offer-tooltip = Withdraws the transfer offer.
drydock-admin-more = ···
drydock-admin-vacate = Vacate berth
drydock-admin-vacate-stored = a stored ship lives in its berth
drydock-admin-vacate-escrow = an escrow ship keeps its berth
drydock-admin-delete-ship = Delete ship record
drydock-admin-reason-placeholder = Reason
drydock-admin-reason-tooltip = Logged with the next action.
drydock-admin-notes-placeholder = Notes

# Every verb is always in the row (2026-09-12); a greyed one carries the reason as its tooltip.
drydock-admin-impound-why-impounded = Already impounded.
drydock-admin-impound-why-escrow = Offer pending. Cancel it first.
drydock-admin-impound-why-terminal = Sold, destroyed or abandoned.
drydock-admin-release-why = Not impounded.
drydock-admin-restore-to-why-stored = Already stored.
drydock-admin-restore-to-why-escrow = Offer pending. Cancel it first.
drydock-admin-restore-to-why-sold = Sold. Use Restore from sale…
drydock-admin-restore-to-why-no-berths = Owner has no berths.
drydock-admin-restore-from-sale-why = Not sold.
drydock-admin-restore-from-sale-why-no-record = No sale on record.
drydock-admin-cancel-offer-why = No pending offer.

# The escrow card: the one fact about a standing offer that the list card and timeline do not show.
drydock-admin-escrow-lands = Lands in:
drydock-admin-escrow-lands-berth = Berth {$berth}
drydock-admin-escrow-lands-none = No free berth

drydock-admin-berths-title = {$owner}'s berths
drydock-admin-berths-count = · {$free} of {$total} free
drydock-admin-grant-berth = Grant berth
drydock-admin-grant-berth-tooltip = Adds a free berth of the chosen class.
drydock-admin-berth-delete = Delete berth
drydock-admin-berth-restore-here = Restore here
drydock-admin-berth-move-here = Move here
drydock-admin-berth-occupied = occupied
drydock-admin-berth-too-small = too small
# An empty berth that is the unberthed selected ship's last: where Release puts it.
drydock-admin-berth-last = empty · {$ship}'s last berth

drydock-admin-revision-appraisal = appraisal {$value}
drydock-admin-promote = Promote revision {$revision}

drydock-admin-notes-title = Notes
drydock-admin-timeline-title = Timeline
drydock-admin-timeline-today = {$time} · today
drydock-admin-refused-tooltip = Sent by a modified client.

drydock-admin-sale-title = Restore {$ship} from sale
drydock-admin-sale-body = Sold for {$price} on {$at}.
drydock-admin-sale-take-back = Take the {$price} back
drydock-admin-sale-take-back-tooltip = Withdraws the sale price from the owner.
drydock-admin-sale-balance = Owner's balance: {$balance}
drydock-admin-sale-balance-unknown = Owner's balance could not be read.
drydock-admin-sale-berth = Berth
drydock-admin-sale-reason-placeholder = Reason (required when the money stays)
drydock-admin-sale-confirm = Restore
drydock-admin-sale-cancel = Cancel

# The impound dialog. The fee is a share of the appraisal; the credits beside the slider are what
# that share comes to, computed the way the server computes it.
drydock-admin-impound-title = Impound {$ship}
drydock-admin-impound-body = {$ship} goes to the impound lot. It holds no berth there.
drydock-admin-impound-fee = Fee
drydock-admin-impound-fee-note = of the {$appraisal} appraisal. 0% is free, 100% is the whole hull.
drydock-admin-impound-fee-note-live = of the {$appraisal} it last appraised at; a hull still in the world is appraised again as it is taken.
drydock-admin-impound-fee-note-unknown = No appraisal is on file for this ship, so any share comes to nothing.
drydock-admin-impound-reason = Reason
drydock-admin-impound-reason-placeholder = What the hull was taken for
drydock-admin-impound-reason-note = Shown to the owner.
drydock-admin-impound-redeemable = Owner can reclaim
drydock-admin-impound-redeemable-detail = · or abandon it instead
drydock-admin-impound-redeemable-note = Untick to hold the hull through an adjudication.
drydock-admin-impound-confirm = Impound
drydock-admin-impound-cancel = Cancel

# One label per audit action, so the timeline reads as prose instead of enum names.
drydock-admin-action-Store = Stored
drydock-admin-action-Retrieve = Retrieved
drydock-admin-action-Restore = Restored
drydock-admin-action-Transfer = Transferred
drydock-admin-action-Delete = Deleted
drydock-admin-action-Rebake = Re-baked
drydock-admin-action-Impound = Impounded
drydock-admin-action-ImpoundReleased = Released
drydock-admin-action-BerthPurchase = Berth purchased
drydock-admin-action-BerthSale = Berth sold
drydock-admin-action-BerthGrant = Berth granted
drydock-admin-action-BerthUpgrade = Berth upgraded
drydock-admin-action-BerthMove = Moved berth
drydock-admin-action-BerthDelete = Berth deleted
drydock-admin-action-Fallback = Fell back to an older revision
drydock-admin-action-AccessRefused = Access refused
drydock-admin-action-TransferOffered = Transfer offered
drydock-admin-action-TransferDeclined = Transfer declined
drydock-admin-action-TransferCancelled = Transfer withdrawn
drydock-admin-action-TransferExpired = Transfer expired
drydock-admin-action-ShipSold = Sold
drydock-admin-action-Renamed = Renamed
drydock-admin-action-SaleReversed = Sale reversed
drydock-admin-action-Imported = Imported
drydock-admin-action-ImpoundRedeemed = Reclaimed
drydock-admin-action-ShipAbandoned = Abandoned
drydock-admin-action-AbandonReversed = Abandon reversed
drydock-admin-action-ShipDestroyed = Written off
drydock-admin-action-ShipStranded = Sweep failed
drydock-admin-action-ClaimReleased = Retrieve failed, returned to storage
drydock-admin-action-RevisionPromoted = Revision promoted
drydock-admin-action-RevisionPinned = Revision pinned
drydock-admin-action-RevisionUnpinned = Revision unpinned
drydock-admin-action-DriftRefused = Retrieve refused, content missing
drydock-admin-action-StateSkipped = Retrieved with state skipped
