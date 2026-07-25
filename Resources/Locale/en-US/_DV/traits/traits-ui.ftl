## Traits Editor UI
trait-editor-title = Character Traits
trait-editor-points-label = Available Points
trait-editor-search-placeholder = Search traits...
trait-editor-footer-hint = Hover over traits for details
# Footer info is chosen at runtime to match the actual limit configuration (global, per-category, or both).
trait-editor-footer-info-global = Limits are set globally
trait-editor-footer-info-category = Limits are set per category
trait-editor-footer-info-both = Limits are set globally and per category

## Disabled Traits Popup
disabled-traits-popup-title = Traits Disabled
disabled-traits-popup-label = Traits Disabled
disabled-traits-popup-message = Some of your selected traits could not be applied because they did not meet the required conditions.
disabled-traits-popup-list-header = The following traits were disabled:
disabled-traits-popup-skip-checkbox = Don't show this again
disabled-traits-popup-close-button = Close

## Disabled Traits Reasons
disabled-traits-reason-global-limit = Global trait limit reached. Deselect a trait to make room.
disabled-traits-reason-points-limit = Not enough global trait points left. Deselect a trait to free some up.
disabled-traits-reason-category-limit = {$category} trait limit reached. Deselect a {$category} trait to make room.
disabled-traits-reason-category-points = Not enough {$category} points left. Deselect a {$category} trait to free some up.
disabled-traits-reason-conflict = Conflicts with selected trait: {$trait}

## Category suffixes
trait-category-traits = {$selected} / {$max} traits
trait-category-traits-unlimited = {$selected} traits
trait-category-points = ({$available} / {$max} pts available)

## Requirements tooltips
trait-requirements-tooltip = [bold]Requirements:[/bold]
    {$requirements}
trait-requirements-not-met-tooltip = Requirements not met:
    {$requirements}

## Requirements tooltips
trait-conflicts-tooltip = [bold]Conflicts:[/bold]
    {$conflicts}
trait-conflicts-met-tooltip = Conflicting traits:
    {$conflicts}

## Condition tooltips
trait-conditions-tooltip = [bold]Conditions:[/bold]
    {$conditions}

## Composite conditions
trait-condition-any-of = Any of the following must be true:
    {$requirements}
trait-condition-all-of = All of the following must be true:
    {$requirements}

## Trait-specific condition tooltips
trait-condition-needs-an-arm = You must have at least one arm.
trait-condition-needs-a-leg = You must have at least one leg.
trait-condition-muted-no-accents = You can't speak, so an accent would do nothing.

## Category conditions
trait-condition-category-has = Must have a {$category} trait.
trait-condition-category-has-not = Must not have a {$category} trait.

## Species conditions
trait-condition-species-is = You must be {INDEFINITE($species)} [color=yellow]{$species}[/color].
trait-condition-species-not = You must not be {INDEFINITE($species)} [color=yellow]{$species}[/color].

## Job conditions
trait-condition-job-is = You must be {INDEFINITE($job)} [color={$color}]{$job}[/color].
trait-condition-job-not = You must not be {INDEFINITE($job)} [color={$color}]{$job}[/color].

## Department conditions
trait-condition-department-is = You must be in the [color={$color}]{$department}[/color] department.
trait-condition-department-not = You must not be in the [color={$color}]{$department}[/color] department.

# Antag conditions
trait-condition-antag-is = Must be eligible for [color=red]{$antag}[/color] antag role.
trait-condition-antag-not = Must not be eligible for [color=red]{$antag}[/color] antag role.

# Trait requirements and conflicts
trait-condition-trait-has-not = Must not have the trait [color=yellow]{$trait}[/color].
trait-condition-trait-has = Must have the trait [color=yellow]{$trait}[/color].
