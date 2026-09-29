"""Dataset adapters: one module per register, behind one candidate shape.

Each adapter pins the export format it was written against and refuses anything else, so a changed
download is a loud rejection rather than a silently empty candidate file (spec-curate.md §The dataset).
"""
