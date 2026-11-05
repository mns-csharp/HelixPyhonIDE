"""Target for Call Hierarchy (incoming calls): `helper` is called from two
top-level functions and one method, so its callers are alpha, beta, Service.run."""


def helper():
    return 1


def alpha():
    return helper() + 1


def beta():
    return helper() * 2


class Service:
    def run(self):
        return helper()
