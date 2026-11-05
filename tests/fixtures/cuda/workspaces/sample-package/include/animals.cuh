#pragma once

#include <string>

class Animal {
public:
    virtual ~Animal() = default;
    virtual std::string speak() const = 0;
};

class Dog : public Animal {
public:
    std::string speak() const override;
};

class Cat : public Animal {
public:
    std::string speak() const override;
};
