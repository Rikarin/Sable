#!/usr/bin/env python3
"""Sable.Serialize's JSON against nst/JSONTestSuite.

    python3 -I tools/conformance/json/run.py <JSONTestSuite>

Every y_ document must be accepted and every n_ one refused, by the reader,
the reader given it in pieces, the parser and the decoder alike; what is
accepted must come back the same from the writer and the printer. An i_ one,
which RFC 8259 leaves to the implementation, must be what DECISIONS says."""

import argparse
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build

# What RFC 8259 leaves open, as decided: the reader takes any number its
# grammar has, as .NET's does, and only a caller that wants an F64 of one
# past them all is refused; text must be well-formed Unicode, so a surrogate
# alone, ill-formed UTF-8, UTF-16 and a byte order mark are refused; arrays
# and objects nest 512 deep.
NUMBER = "accept, but not as a tree"
DECISIONS = {
    "i_number_double_huge_neg_exp": "accept",
    "i_number_huge_exp": NUMBER,
    "i_number_neg_int_huge_exp": NUMBER,
    "i_number_pos_double_huge_exp": NUMBER,
    "i_number_real_neg_overflow": NUMBER,
    "i_number_real_pos_overflow": NUMBER,
    "i_number_real_underflow": "accept",
    "i_number_too_big_neg_int": "accept",
    "i_number_too_big_pos_int": "accept",
    "i_number_very_big_negative_int": "accept",
    "i_object_key_lone_2nd_surrogate": "refuse",
    "i_string_1st_surrogate_but_2nd_missing": "refuse",
    "i_string_1st_valid_surrogate_2nd_invalid": "refuse",
    "i_string_UTF-16LE_with_BOM": "refuse",
    "i_string_UTF-8_invalid_sequence": "refuse",
    "i_string_UTF8_surrogate_U+D800": "refuse",
    "i_string_incomplete_surrogate_and_escape_valid": "refuse",
    "i_string_incomplete_surrogate_pair": "refuse",
    "i_string_incomplete_surrogates_escape_valid": "refuse",
    "i_string_invalid_lonely_surrogate": "refuse",
    "i_string_invalid_surrogate": "refuse",
    "i_string_invalid_utf-8": "refuse",
    "i_string_inverted_surrogates_U+1D11E": "refuse",
    "i_string_iso_latin_1": "refuse",
    "i_string_lone_second_surrogate": "refuse",
    "i_string_lone_utf8_continuation_byte": "refuse",
    "i_string_not_in_unicode_range": "refuse",
    "i_string_overlong_sequence_2_bytes": "refuse",
    "i_string_overlong_sequence_6_bytes": "refuse",
    "i_string_overlong_sequence_6_bytes_null": "refuse",
    "i_string_truncated-utf-8": "refuse",
    "i_string_utf16BE_no_BOM": "refuse",
    "i_string_utf16LE_no_BOM": "refuse",
    "i_structure_500_nested_arrays": "accept",
    "i_structure_UTF-8_BOM_empty_object": "refuse",
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=pathlib.Path)
    arguments = parser.parse_args()
    paths = sorted((arguments.data / "test_parsing").glob("*.json"))
    answers = ask(build(pathlib.Path(__file__).resolve().parent / "answer.sb"), [str(path.resolve()) for path in paths])
    tally = Tally()
    for path, answer in zip(paths, answers):
        kind = path.name[0]
        wanted = {"y": "accept", "n": "refuse"}.get(kind) or DECISIONS.get(path.stem, "a decision")
        got = "refuse" if answer.startswith("refuse ") else answer
        tally.check(f"{kind}_", got == wanted, f"{path.name}: {answer[:200]}, not {wanted}")
    tally.finish()


main()
