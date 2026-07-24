"""Wire-safe decimal parsing shared by rule validation and evaluation."""

from __future__ import annotations

import re
from decimal import Decimal

MAX_DECIMAL_PRECISION = 38
MAX_DECIMAL_SCALE = 18
MAX_DECIMAL_INTEGER_DIGITS = 20
DECIMAL_WIRE_FORMAT = "canonical-decimal-string"

_DECIMAL_PATTERN = re.compile(r"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$")


class DecimalWireError(ValueError):
    """A decimal value does not satisfy the versioned JSON wire contract."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def parse_decimal_wire(value: object) -> tuple[str, Decimal]:
    """Parse and canonicalize a bounded, non-exponent decimal JSON string.

    JSON numbers are deliberately rejected: they may already have lost
    precision in a JavaScript or binary-float transport before this function
    sees them.
    """
    if not isinstance(value, str):
        raise DecimalWireError(
            "invalid_decimal_type",
            "Decimal values must be JSON strings, not JSON numbers.",
        )
    if not _DECIMAL_PATTERN.fullmatch(value):
        raise DecimalWireError(
            "invalid_decimal_format",
            "Decimal strings must use plain base-10 notation without exponent, "
            "plus sign, or leading zeros.",
        )

    unsigned = value[1:] if value.startswith("-") else value
    integer_part, separator, fraction_part = unsigned.partition(".")
    if len(integer_part) > MAX_DECIMAL_INTEGER_DIGITS:
        raise DecimalWireError(
            "decimal_out_of_range",
            f"Decimal integer digits exceed the {MAX_DECIMAL_INTEGER_DIGITS} digit limit.",
        )
    if separator and len(fraction_part) > MAX_DECIMAL_SCALE:
        raise DecimalWireError(
            "decimal_scale_exceeded",
            f"Decimal scale exceeds the {MAX_DECIMAL_SCALE} digit limit.",
        )
    significant_digits = (integer_part.lstrip("0") + fraction_part).lstrip("0")
    if len(significant_digits) > MAX_DECIMAL_PRECISION:
        raise DecimalWireError(
            "decimal_precision_exceeded",
            f"Decimal precision exceeds the {MAX_DECIMAL_PRECISION} digit limit.",
        )

    decimal_value = Decimal(value)
    normalized_fraction = fraction_part.rstrip("0")
    canonical = integer_part
    if normalized_fraction:
        canonical += f".{normalized_fraction}"
    if value.startswith("-") and decimal_value != 0:
        canonical = f"-{canonical}"
    return canonical, decimal_value
