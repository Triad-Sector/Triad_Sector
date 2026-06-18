# Triad: generator for the accent torture corpus.
# Produces a ~1000-line text file designed to (a) trip every accent's regex edge cases
# (mid-word th, intervocalic vs coda r, -ing keep-list, tt glottal, -er endings, ALL CAPS,
# sentence-initial conjunctions, contractions, proper nouns/acronyms, numbers, punctuation,
# apostrophe-internal words) and (b) exercise the dialect vocabulary via station chatter.
# Run once; the output is committed and read by AccentTortureRigTest. Deterministic seed.
#
#   pwsh Tools/Accents/Gen-TortureCorpus.ps1

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\..\Content.IntegrationTests\Tests\AccentTorture\corpus.txt'
$outDir = Split-Path $out -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# Deterministic randomness so re-running yields the same corpus (diff-friendly).
$rng = [System.Random]::new(0x71AD)

# --- Block A+C: hand-written edge-case traps and dense prose (highest-value torture lines). ---
$hand = @'
The king brought a string, a ring, and one shiny thing to the spring fling.
Matthew lit the bathtub last month, humming a rhythm to the anthem.
An honest heir spent an hour in honor of the honest, hard-working hour.
Carry the very sorry arrow; marry in a hurry, not in a furry burrow.
The hard car parked far from the harbor water tower near the marsh.
Attack the button, the better bottle, the little kitty, no matter what.
The paper, the water, the computer, the officer, my mother and my brother.
STOP RIGHT THERE, YOU ABSOLUTE FOOL, AND DROP THE CROWBAR THIS INSTANT!
And then the captain spoke. The doctor agreed. But the warden said nothing.
I'm sure you're right; they aren't here and it isn't easy. I would've stayed.
Dr. Smith from NASA called Mr. Jones at the FBI about McCargo and the TSFMC.
I have 5 credits, 100 spesos, 3 bottles, 10 friends, and level 42 clearance.
Wait... what?! Stop; listen, and think (carefully) about this whole thing.
It's six o'clock; rock 'n' roll, y'all, ain't it grand, brother?
Hello there, officer, I think the thief took my money through the door.
Whatever you saw, say nothing; however hard they look, they catch nothing.
The mother thanked the father, the brother, and the other smother of bothers.
Thirty thieves thought they thrived through thick and thin on Thursday.
Why would the wily warden wander west with weary, watchful, wicked eyes?
She sells seashells by the seashore, surely something special, sir.
Vince viewed the vividville, vexed, voicing every vague vendetta.
Ze healing is not as rewarding as ze hurting, but I will heal you anyway.
Just give me a little water and a bottle of beer before the long sleep.
Going to the docks, drinking, smoking, and watching the shuttles fly away.
The quick brown fox jumps over the lazy dog without making any sense.
Doctor, doctor, my head hurts, my legs hurt, and my hands are very cold.
Run, hide, follow, fight: the security officer is hunting the clown again.
Could you write it down on paper? I cannot hear you over the loud machine.
We were never going to win, but we tried, and we lost, and we laughed.
Father told mother that brother stole the other money from the bank vault.
HEY! GET BACK HERE WITH MY DUFFEL BAG AND MY HARD-EARNED CREDITS, THIEF!
A united union of unique unicorns used a useful uniform at the university.
He hit the helmet, held the hose, heard the horror, and hoped for heaven.
Knock down the door, knock out the warden, and knock off the whole heist.
Thirty-three thirsty thugs threw three thousand thumbtacks through Thelma.
The little bottle of bitter butter made the batter better, no matter what.
Officer, the pilot, the medic, and the scientist all watched the murder.
'Ello, 'ow are ya? Wiv a bo'le of lush an' a bi' of grub, innit, mate?
Aye, laddie, dinnae fash yerself; the wee bairn cannae ken the loch yet.
Howdy, partner, I reckon we ought to mosey on down yonder before sundown.
Arrr, ye scurvy landlubber, hand over the booty or walk the bloody plank!
Crikey, mate, that drongo nicked me esky and shot through to the servo.
Choom, the gonk netrunner flatlined; grab the eddies and delta outta here.
Me no like big word; me smash rock, eat meat, sleep, make fire, hunt now.
Bonjour, ze captain is wiz ze doctor, non? Such an embarrassing little man.
What is this thing that they call the truth? Nothing but theater, I think.
The thunderous weather brought neither warmth nor worth to the northern moor.
Eleven elephants entered the elegant elevator, eager, elated, and edgy.
Buttery batter splattered the gutter while the otter shuttered the cutter.
Persistent performers prefer perfect performances over poorer rehearsals.
Singing, ringing, bringing, stinging: the swinging strings keep clinging on.
You took the booze, the boots, the bag, and the bottle, you absolute clown.
Was it the warden, the jailer, the bailiff, or the templar who arrested me?
Twenty-two bottles, forty-four boots, and sixty-six crowbars went missing.
"Stop right there!" she shouted, but he kept running through the dark hall.
Honestly, the honorable heir honored the hourly honesty of the honest hen.
Computers, printers, scanners, toasters, blenders: the engineers fixed them.
The captain's quarters, the warden's office, and the doctor's bed are locked.
My, oh my, what a hot, hard, harrowing, horrible, hideous hospital this is.
Either you talk, or they walk; rather, neither shall pass without papers.
'@

# --- Block B: station chatter templates x dialect vocabulary banks. ---
$greetings = @('Hello', 'Hey', 'Oi', 'Listen', 'Look', 'Attention', 'Excuse me', 'Right')
$roles     = @('captain', 'officer', 'doctor', 'warden', 'pilot', 'scientist', 'clown', 'mercenary',
               'security guard', 'medic', 'engineer', 'chef', 'miner', 'lawyer', 'chaplain',
               'quartermaster', 'jailer', 'bailiff', 'reporter', 'pirate', 'merc')
$items     = @('crowbar', 'bottle', 'wrench', 'gun', 'knife', 'toolbox', 'duffel bag', 'helmet',
               'money', 'papers', 'credits', 'beer', 'tobacco', 'shoes', 'handcuffs', 'corpse',
               'emitter', 'bottle of booze', 'bag of cash', 'pile of credits')
$places    = @('the bridge', 'medical', 'the brig', 'engineering', 'the bar', 'the docks',
               'the harbour', 'cargo', 'the shuttle', 'the prison', 'the hospital', 'the diner',
               'the restaurant', 'the depot', 'security', 'their quarters', 'the airlock')
$verbs     = @('steal', 'attack', 'watch', 'follow', 'hide', 'kill', 'beat', 'rob', 'arrest',
               'harass', 'smoke', 'drink', 'hold', 'give', 'take', 'murder', 'destroy', 'trick')
$verbsIng  = @('stealing', 'attacking', 'watching', 'following', 'hiding', 'killing', 'beating',
               'robbing', 'arresting', 'harassing', 'smoking', 'drinking', 'holding', 'giving',
               'taking', 'murdering', 'destroying', 'tricking', 'running', 'thinking', 'talking')

$templates = @(
    '{G}, the {R} is {VI} the {I} over by {P}.',
    'Did you {V} the {R}? They took my {I} and ran to {P}.',
    'Watch out, the {R} has a {I}! Get to {P} now.',
    'I think the {R} and the {R2} are {VI} something near {P}.',
    'Why would a {R} {V} the {I}? That makes no sense at all.',
    '{G}! Stop {VI} my {I} or I will call the {R} to {P}.',
    'The {R} said the {R2} was {VI} the {I} in {P} again.',
    'Everything in {P} is gone: the {I}, the {I2}, and the other {I3}.',
    'You are going to {V} the {I}, then meet the {R} at {P}, understand?',
    'However hard the {R} tries, they cannot {V} the {I} without {P}.',
    'That {R} is a thief; they stole the {I} and hid it somewhere in {P}.',
    '{G}, brother, give me the {I} before the {R} starts {VI} again.'
)

function Pick($arr) { return $arr[$rng.Next($arr.Length)] }

$generated = New-Object System.Collections.Generic.HashSet[string]
$guard = 0
while ($generated.Count -lt 1100 -and $guard -lt 200000) {
    $guard++
    $t = Pick $templates
    $line = $t.
        Replace('{G}', (Pick $greetings)).
        Replace('{R2}', (Pick $roles)).
        Replace('{R}', (Pick $roles)).
        Replace('{VI}', (Pick $verbsIng)).
        Replace('{V}', (Pick $verbs)).
        Replace('{I3}', (Pick $items)).
        Replace('{I2}', (Pick $items)).
        Replace('{I}', (Pick $items)).
        Replace('{P}', (Pick $places))
    # Occasionally shout a line or strip terminal punctuation, to torture caps/affix handling.
    $roll = $rng.Next(20)
    if ($roll -eq 0) { $line = $line.ToUpperInvariant() }
    elseif ($roll -eq 1) { $line = $line.TrimEnd('.', '!', '?') }
    [void]$generated.Add($line)
}

$handLines = $hand -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ -ne '' }

$all = @()
$all += $handLines
$all += @($generated)
$all = $all | Select-Object -First 1000

Set-Content -Path $out -Value $all -Encoding utf8
Write-Output "Wrote $($all.Count) lines to $out"
