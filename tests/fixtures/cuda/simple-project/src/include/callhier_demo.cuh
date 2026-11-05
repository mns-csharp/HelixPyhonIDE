#pragma once

// Target for Call Hierarchy (incoming calls): `helper` is called from two
// free functions and one method, so its callers are alpha, beta, Service::run.
namespace pythonsimple::callhier_demo {

int helper();
int alpha();
int beta();

class Service {
public:
    int run() const;
};

} // namespace pythonsimple::callhier_demo
