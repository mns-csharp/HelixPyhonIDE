#pragma once

// 3-level hierarchy for Go To Implementation.
//
// Service (abstract) -> BaseService (abstract) -> SqlService (concrete).
// Go To Implementation on Service must reach SqlService (the concrete leaf)
// transitively, skipping the abstract BaseService — unlike Go To Derived,
// which stops at BaseService.
namespace pythonsimple::impl_demo {

class Service {
public:
    virtual ~Service() = default;
    virtual int run() const = 0;
};

class BaseService : public Service {
public:
    virtual bool validate() const = 0;
    int helper() const;
};

class SqlService : public BaseService {
public:
    int run() const override;
    bool validate() const override;
};

} // namespace pythonsimple::impl_demo
