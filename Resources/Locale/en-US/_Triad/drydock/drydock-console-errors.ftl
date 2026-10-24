# Triad: drydock console failures, written to the chat of the player who pressed and nobody else.
# Successes write nothing. Every line opens with its verb label ($verb) and names one reason.

## Verb labels

drydock-error-verb-store = Store
drydock-error-verb-retrieve = Retrieve
drydock-error-verb-purchase = Purchase
drydock-error-verb-berth-grant = Berth grant
drydock-error-verb-buy-berth = Berth purchase
drydock-error-verb-sell-berth = Berth sale
drydock-error-verb-upgrade-berth = Berth upgrade
drydock-error-verb-offer = Transfer offer
drydock-error-verb-cancel-offer = Cancel
drydock-error-verb-decline-offer = Decline
drydock-error-verb-accept-offer = Accept
drydock-error-verb-sell = Sale
drydock-error-verb-rename = Rename
drydock-error-verb-move = Move
drydock-error-verb-reclaim = Reclaim
drydock-error-verb-abandon = Abandon
drydock-error-verb-reissue-deed = Deed transfer
drydock-error-verb-deed-action = Deed action

## Shared

drydock-error-exception = {$verb} failed: an error occurred. It has been logged.
drydock-error-no-account = {$verb} failed: no player account is attached to you.
drydock-error-barred = {$verb} failed: your role is barred from the drydock.
drydock-error-not-owner = {$verb} failed: that ship belongs to another captain.
drydock-error-no-station = {$verb} failed: this console is not on a station.
drydock-error-no-bank = {$verb} failed: you have no bank account.
drydock-error-funds = {$verb} failed: not enough funds, it costs {$price}.
drydock-error-interrupted = {$verb} failed: interrupted before it finished.
drydock-error-name-mismatch = {$verb} failed: the typed name does not match the ship's name.

## Cards

drydock-error-no-card = {$verb} failed: insert an ID card first.
drydock-error-card-not-id = {$verb} failed: the inserted card is not an ID card.
drydock-error-card-voucher = {$verb} failed: a voucher cannot carry a deed.
drydock-error-card-has-deed = {$verb} failed: the inserted card already carries a deed.

## Ships and berths

drydock-error-ship-not-found = {$verb} failed: that ship is not on your account.
drydock-error-ship-not-stored = {$verb} failed: that ship is not stored.
drydock-error-ship-not-impounded = {$verb} failed: that ship is not impounded.
drydock-error-berth-not-yours = {$verb} failed: that berth is not on your account.
drydock-error-berth-or-ship-not-yours = {$verb} failed: that ship or berth is not on your account.
drydock-error-berth-class-unknown = {$verb} failed: that berth class is not recognized.
drydock-error-berth-no-berth = {$verb} failed: no free berth.
drydock-error-berth-too-small = {$verb} failed: no free berth fits the hull.
drydock-error-named-berth-too-small = {$verb} failed: that berth is too small for the hull.
drydock-error-berth-occupied = {$verb} failed: that berth already holds a ship.
drydock-error-berth-wrong-state = {$verb} failed: that ship is not in a state that allows it.
drydock-error-berth-conflict = {$verb} failed: another change landed at the same moment.

## Purchase

drydock-error-purchase-ship-out = {$verb} failed: you already have a ship underway.
drydock-error-berth-grant-failed = {$verb} failed: the berth that came with your ship could not be recorded. It has been logged.

## Store

drydock-error-store-no-card = {$verb} failed: insert the card carrying the ship's deed.
drydock-error-store-no-deed = {$verb} failed: the inserted card carries no deed to a ship.
drydock-error-store-unowned = {$verb} failed: no account owns that ship.
drydock-error-store-faction-ship = {$verb} failed: faction vessels cannot be stored.
drydock-error-store-voucher-ship = {$verb} failed: a ship bought on a voucher cannot be stored.
drydock-error-store-not-docked = {$verb} failed: dock the ship at this station first.
drydock-error-store-serialize-failed = {$verb} failed: the ship could not be saved.
drydock-error-store-organics-aboard = {$verb} failed: someone alive is still aboard.
drydock-error-store-hazard-aboard = {$verb} failed: an armed nuke, an active countdown or a singularity is aboard.
drydock-error-store-validation-failed = {$verb} failed: the saved copy did not match the ship. It has been logged.
drydock-error-store-disabled = {$verb} failed: the drydock is not accepting ships right now.
drydock-error-store-no-berth = {$verb} failed: no free berth of yours is available.
drydock-error-store-berth-too-small = {$verb} failed: the hull is too large for the berth.
drydock-error-store-in-progress = {$verb} failed: that ship is already being stored.

## Retrieve

drydock-error-retrieve-ship-out = {$verb} failed: another of your ships is already underway.
drydock-error-retrieve-disabled = {$verb} failed: the drydock is off.
drydock-error-retrieve-no-dock-grid = {$verb} failed: this station has no grid to dock the ship at.
drydock-error-retrieve-no-staging-map = {$verb} failed: no staging map was available to load the ship onto.
drydock-error-retrieve-not-found = {$verb} failed: that ship's record or saved copy is missing.
drydock-error-retrieve-already-out = {$verb} failed: that ship is already out.
drydock-error-retrieve-impounded = {$verb} failed: that ship is in the impound lot.
drydock-error-retrieve-in-escrow = {$verb} failed: that ship is offered to another captain.
drydock-error-retrieve-sold = {$verb} failed: that ship was sold.
drydock-error-retrieve-not-stored = {$verb} failed: another retrieve of that ship got there first.
drydock-error-retrieve-unreadable = {$verb} failed: no saved copy of that ship would load. It has been logged.
drydock-error-retrieve-station-lost = {$verb} failed: the station was lost while the ship was loading.
drydock-error-retrieve-destroyed = {$verb} failed: that ship was written off.
drydock-error-retrieve-abandoned = {$verb} failed: that ship was abandoned.
drydock-error-retrieve-content-drift = {$verb} failed: that ship references content that no longer exists. It has been logged.

## Berths

drydock-error-buy-berth-not-for-sale = {$verb} failed: that berth class has no price set.
drydock-error-sell-berth-occupied = {$verb} failed: move the ship out of that berth first.
drydock-error-upgrade-berth-largest = {$verb} failed: that berth is already the largest class.
drydock-error-upgrade-berth-already = {$verb} failed: that berth has already been upgraded.

## Transfers

drydock-error-offer-self = {$verb} failed: you cannot offer a ship to yourself.
drydock-error-offer-recipient-offline = {$verb} failed: that captain is not online.
drydock-error-offer-recipient-no-berth = {$verb} failed: that captain has no free berth.
drydock-error-offer-recipient-too-small = {$verb} failed: none of that captain's free berths fits the hull.
drydock-error-offer-conflict = {$verb} failed: another offer on that ship landed first.
drydock-error-offer-not-open = {$verb} failed: that offer is no longer open.
drydock-error-offer-not-made-by-you = {$verb} failed: you did not make that offer.
drydock-error-offer-not-addressed = {$verb} failed: that offer is not addressed to you.
drydock-error-accept-expired = {$verb} failed: the offer expired or was withdrawn.
drydock-error-accept-no-berth = {$verb} failed: you have no free berth.
drydock-error-accept-berth-too-small = {$verb} failed: none of your free berths fits the hull.
drydock-error-accept-conflict = {$verb} failed: your free berth was taken at the same moment.

## Sale and rename

drydock-error-sell-no-appraisal = {$verb} failed: that ship has no appraisal on file.
drydock-error-sell-store-first = {$verb} failed: store the ship, then sell it from the drydock tab.
drydock-error-rename-invalid = {$verb} failed: use 1 to {$max} letters, digits, spaces or dashes.

## Impound lot

drydock-error-impound-locked = {$verb} failed: an admin is holding that ship.
drydock-error-impound-changed = {$verb} failed: that ship is no longer impounded, or an admin is holding it.
drydock-error-reclaim-funds = {$verb} failed: not enough funds, reclaiming it costs {$fee}.
drydock-error-reclaim-fee-changed = {$verb} failed: the fee changed since the console showed it.

## Deed transfer

drydock-error-reissue-ship-gone = {$verb} failed: that ship no longer exists or has no owner.
drydock-error-reissue-storing = {$verb} failed: that ship is being stored.
drydock-error-reissue-provisioned = {$verb} failed: faction and voucher ships keep the deed they were issued with.
