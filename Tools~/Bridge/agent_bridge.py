#!/usr/bin/env python3
"""Start the Balloon Game Agent API MCP bridge from a file path (no PYTHONPATH needed).

    python agent_bridge.py --project <Unity project> --tools <tools.json>

This is the same as `python -m automata_bridge ...` run from this folder.
See README.md next to this file for the arguments.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from automata_bridge.__main__ import main  # noqa: E402

if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
