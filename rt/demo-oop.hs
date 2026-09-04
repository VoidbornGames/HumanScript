// Gen 7 tour: enums, structs, classes, nullables, generics, conversions.

enum Mood { Grumpy, Happy = 5, Sleepy }

struct Point
{
    int X;
    int Y;

    int Manhattan()
    {
        var total = X + Y;
        return total;
    }
}

class User
{
    string Name;
    int Age;
    Mood Mood;

    public string Describe()
    {
        return Name + " (" + string(Age) + ", mood " + string(int(Mood)) + ")";
    }

    public void Birthday()
    {
        Age = Age + 1;
        Mood = Mood.Happy;
    }

    public T Choose<T>(T onHappy, T onGrumpy)
    {
        if (Mood == Mood.Happy) { return onHappy; }
        return onGrumpy;
    }
}

T Newer<T>(T a, T b, int pick)
{
    if (pick > 0) { return a; }
    return b;
}

string Describe(User? u)
{
    return u?.Describe() ?? "nobody";
}

var origin = Point { X: 3, Y: 4 };
print(origin.Manhattan());
origin.X = 10;
print(origin.X + origin.Y);

var u = User { Name: "Ada", Age: 36, Mood: Mood.Grumpy };
u.Birthday();
print(u.Describe());
print(u.Choose<string>("yay", "meh"));
print(u.Choose(1, 2));

User? missing = null;
print(Describe(missing));
print(Describe(u));

int? maybe = null;
if (float(7) > 7.4)
{
    maybe = null;
}
else
{
    maybe = 8;
}
print(maybe ?? -1);

var pick = Newer<int>(Newer(4, 5, 1), Newer(6, 7, 0), 0);
print(pick);
print(int("42") + int(2.9));
print(Mood.Sleepy);
print(mem());
