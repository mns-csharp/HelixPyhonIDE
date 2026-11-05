#include "app_cli/pipeline.cuh"

// pipeline.cuh is header-only (every method is a template defined
// in-class); this translation unit exists so the app_cli target has a
// concrete .cu source mirroring pipeline.py, and exercises
// make_demo_pipeline() at namespace scope to prove the whole template
// chain instantiates cleanly.
namespace app_cli::pipeline {

namespace {
[[maybe_unused]] Pipeline<int, int> demo_instance = make_demo_pipeline();
}  // namespace

}  // namespace app_cli::pipeline
