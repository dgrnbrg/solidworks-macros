"""Run the repository VBA static linter and unit/geometry test suite."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(TESTS_DIR))

import lint_vba  # noqa: E402


def main() -> int:
    print("=== Step 1: VBA Static Linter ===")
    lint_rc = lint_vba.main()
    if lint_rc != 0:
        return lint_rc

    print("\n=== Step 2: Unit & Geometry Tests ===")
    suite = unittest.defaultTestLoader.discover(str(TESTS_DIR), pattern="test_*.py")
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
