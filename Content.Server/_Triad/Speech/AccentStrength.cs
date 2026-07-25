/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
namespace Content.Server._Triad.Speech;

/// <summary>
/// Triad: accent intensity tier. Thick (default) runs the full enriched accent; Slight runs the lighter,
/// more intelligible variant (slim word list, gated phonetics, heavy passes excluded).
/// </summary>
public enum AccentStrength : byte
{
    Thick = 0,
    Slight = 1,
}
