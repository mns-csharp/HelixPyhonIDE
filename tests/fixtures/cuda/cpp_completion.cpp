#include <algorithm>
#include <memory>
#include <vector>

struct Particle
{
    float energy;
};

float total_energy(const std::vector<Particle>& particles)
{
    float total = 0.0f;
    for (const Particle& particle : particles)
        total += particle.energy;
    return total;
}

int main()
{
    auto particle = std::make_unique<Particle>();
    particle->energy = 42.0f;
    return particle->energy > 0.0f ? 0 : 1;
}
