"""Deterministic target for exercising the debugger's Variables tree-expansion.

Builds a list, a nested dict, and an object instance, then stops on `return total`
with all of them in scope so the GUI Variables panel can expand each one.
"""


class Point:
    def __init__(self, x: int, y: int):
        self.x = x
        self.y = y


def build():
    numbers = [10, 20, 30]
    person = {"name": "Ada", "age": 36, "langs": ["py", "c"]}
    p = Point(3, 4)
    total = sum(numbers)
    return total  # breakpoint here: numbers/person/p/total all in scope


if __name__ == "__main__":
    print("TOTAL", build())
