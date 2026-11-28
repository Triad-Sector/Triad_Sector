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

drydock-error-exception = An error occurred. It has been logged.
drydock-error-no-account = No player account is attached to you.
drydock-error-barred = Error: You are not authorized to use the drydock.
drydock-error-not-owner = Error: Unauthorized retrieval detected!
drydock-error-no-station = This console is not on a station.
drydock-error-no-bank = Error: You have no filed bank account.
drydock-error-funds = Not enough funds, it costs {$price}.
drydock-error-interrupted = Operation interrupted before it finished.
drydock-error-name-mismatch = The typed name does not match the ship's name.

## Cards

drydock-error-no-card = Error: Please insert an ID card first.
drydock-error-card-not-id = Error: The inserted card is not an ID card.
drydock-error-card-voucher = Error: Vouchers are not authorized for ship deeds.
drydock-error-card-has-deed = Error: The inserted card already has a ship deed.

## Ships and berths

drydock-error-ship-not-found = Error: That ship is not on your account.
drydock-error-ship-not-stored = Error: That ship is not stored.
drydock-error-ship-not-impounded = Error: That ship is not impounded.
drydock-error-berth-not-yours = Error: That berth is not on your account.
drydock-error-berth-or-ship-not-yours = Error: That ship or berth is not on your account.
drydock-error-berth-class-unknown = Error: Unknown berth class.
drydock-error-berth-no-berth = Error: No free berths detected.
drydock-error-berth-too-small = Error: No free berth is able to fit this hull.
drydock-error-named-berth-too-small = Error: That berth does not support that hull size.
drydock-error-berth-occupied = Error: That berth is occupied!
drydock-error-berth-wrong-state = Error: That ship is not in a state that allows it.
drydock-error-berth-conflict = Error: Ship conflicts

## Purchase

drydock-error-purchase-ship-out = Error: You already have a ship underway.
drydock-error-berth-grant-failed = Error: The berth that came with your ship could not be recorded. It has been logged.

## Store

drydock-error-store-no-card = Error: No ID card detected!
drydock-error-store-no-deed = Error: The inserted ID card does not own a ship.
drydock-error-store-unowned = Error: No account owns that ship.
drydock-error-store-faction-ship = Error: Attempted to store illegal ship. Authorities have been contacted.
drydock-error-store-voucher-ship = Error: Vouchers cannot be stored.
drydock-error-store-not-docked = Error: Ship was not docked.
drydock-error-store-serialize-failed = Ship could not be saved.
drydock-error-store-organics-aboard = Error: Someone alive is still aboard!
drydock-error-store-hazard-aboard = Error: A hazard is aboard the ship!
drydock-error-store-validation-failed = The saved copy did not match the ship. It has been logged.
drydock-error-store-disabled = Error: The drydock is not accepting ships at the moment.
drydock-error-store-no-berth = Error: No free berths detected on user account.
drydock-error-store-berth-too-small = Error: The hull is too large for the berth.
drydock-error-store-in-progress = Error: That ship is already being stored.

## Retrieve

drydock-error-retrieve-ship-out = Error: A ship is already underway.
drydock-error-retrieve-disabled = Error: The drydock is not accepting ship retrievals at the moment.
drydock-error-retrieve-no-dock-grid = Error: No hull detected.
drydock-error-retrieve-no-staging-map = No staging map was available to load the ship onto.
drydock-error-retrieve-not-found = Error: That ship's record or saved copy is missing.
drydock-error-retrieve-already-out = Error: That ship is already out.
drydock-error-retrieve-impounded = Error: That ship is in the impound lot.
drydock-error-retrieve-in-escrow = Error: That ship is in transit to another captain.
drydock-error-retrieve-sold = Error: That ship was sold.
drydock-error-retrieve-not-stored = Error: Another retrieve of that ship was stored first.
drydock-error-retrieve-unreadable = No saved copy of that ship would load. It has been logged.
drydock-error-retrieve-station-lost = The station was lost while the ship was loading.
drydock-error-retrieve-destroyed = Error: That ship was written off.
drydock-error-retrieve-abandoned = Error: That ship was abandoned.
drydock-error-retrieve-content-drift = That ship references content that no longer exists. It has been logged.

## Berths

drydock-error-buy-berth-not-for-sale = Error: That berth class is not for sale.
drydock-error-sell-berth-occupied = Error: Berth is occupied.
drydock-error-upgrade-berth-largest = Error: That berth is already the largest class.
drydock-error-upgrade-berth-already = Error: That berth has already been upgraded.

## Transfers

drydock-error-offer-self = Error: You cannot offer a ship to yourself.
drydock-error-offer-recipient-offline = Error: That captain is not active.
drydock-error-offer-recipient-no-berth = Error: That captain has no free berth.
drydock-error-offer-recipient-too-small = Error: None of that captain's free berths fits the hull.
drydock-error-offer-conflict = Error: Another offer on that ship landed first.
drydock-error-offer-not-open = Error: That offer is no longer open.
drydock-error-offer-not-made-by-you = Error: You did not make that offer.
drydock-error-offer-not-addressed = Error: that offer is not addressed to you.
drydock-error-accept-expired = Error: That offer expired or was withdrawn.
drydock-error-accept-no-berth = Error: You have no free berth.
drydock-error-accept-berth-too-small = Error: No available berths are able to fit this size of hull.
drydock-error-accept-conflict = Error: Your free berth was taken.

## Sale and rename

drydock-error-sell-no-appraisal = Error: That ship has no appraisal on file.
drydock-error-sell-store-first = Error: The ship must be stored first.
drydock-error-rename-invalid = Use 1 to {$max} letters, digits, spaces or dashes.

## Impound lot

drydock-error-impound-locked = An admin is holding that ship.
drydock-error-impound-changed = That ship is no longer impounded, or an admin is holding it.
drydock-error-reclaim-funds = Error: You do not have enough funds. Reclaiming it costs {$fee}.
drydock-error-reclaim-fee-changed = The fee updated since the console showed it.

## Deed transfer

drydock-error-reissue-ship-gone = That ship no longer exists or has no owner.
drydock-error-reissue-storing = Error: that ship is being stored.
drydock-error-reissue-provisioned = Error: Unauthorized deed transfer detected.
