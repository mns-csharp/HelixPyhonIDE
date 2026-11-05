"""3-level hierarchy for Go To Implementation.

Service (abstract) -> BaseService (abstract) -> SqlService (concrete).
Go To Implementation on Service must reach SqlService (the concrete leaf) transitively,
skipping the abstract BaseService — unlike Go To Derived, which stops at BaseService.
"""

from abc import ABC, abstractmethod


class Service(ABC):
    @abstractmethod
    def run(self):
        ...


class BaseService(Service, ABC):
    @abstractmethod
    def validate(self):
        ...

    def helper(self):
        return 1


class SqlService(BaseService):
    def run(self):
        return self.helper()

    def validate(self):
        return True
