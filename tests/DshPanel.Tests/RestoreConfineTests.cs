using DshPanel.Isolation;
using DshPanel.Restore;
using Xunit;

namespace DshPanel.Tests;

/// <summary>
/// ГРАНИЦА ИЗОЛЯЦИИ ДЛЯ НАКАТА — чистая проверка, без диска и без сети.
///
/// Правило одно и короткое: в изолированном прогоне ни одна группа не раскладывается за пределы
/// корня прогона. Понадобилось оно потому, что цели групп движка и Node накат берёт НЕ из корня,
/// а с машины — <c>%APPDATA%\npm</c> и каталог Node, то есть глобальная установка владельца.
/// Пока копию раскладывал человек из окна, это было решением о его же машине; с ключом
/// <c>--restore --with-engine</c> то же самое делает скрипт из ИЗОЛИРОВАННОГО прогона.
/// </summary>
public class RestoreConfineTests
{
    private static AppPaths Paths() => AppPaths.Under(@"C:\Temp\dsh-confine-probe");

    /// <summary>
    /// Цель вне корня в изоляции НЕ разрешается, цель под корнем — разрешается. Проверяются обе
    /// ветки, которыми идут группы движка и Node: чужой каталог машины и место под корнем.
    /// </summary>
    [Fact]
    public void В_изоляции_цель_вне_корня_не_разрешается()
    {
        // Ветка «цель — место ЭТОЙ машины»: движок и Node лежат в её глобальной установке.
        Assert.False(RestoreConfine.Allows(@"C:\Program Files\nodejs", Paths(), isolated: true));
        Assert.False(RestoreConfine.Allows(@"C:\Users\Кто-то\AppData\Roaming\npm", Paths(), isolated: true));

        // Другой корень, начинающийся теми же буквами, — НЕ внутри: сверка по границе пути,
        // а не по подстроке (иначе соседняя папка считалась бы своей).
        Assert.False(RestoreConfine.Allows(@"C:\Temp\dsh-confine-probe-рядом", Paths(), isolated: true));

        // А это — под корнем прогона: так выглядят цели у прогона со СВОИМ движком,
        // и раскладывать их не только можно, но и нужно.
        Assert.True(RestoreConfine.Allows(@"C:\Temp\dsh-confine-probe\engine\npm", Paths(), isolated: true));
        Assert.True(RestoreConfine.Allows(@"C:\Temp\dsh-confine-probe\nodejs", Paths(), isolated: true));

        // Корень — тоже «не за пределы»: цель, равная корню, из него не выходит.
        Assert.True(RestoreConfine.Allows(@"C:\Temp\dsh-confine-probe", Paths(), isolated: true));
        Assert.True(RestoreConfine.Allows(@"C:\Temp\dsh-confine-probe\", Paths(), isolated: true));

        // Пустая цель — группы нет вовсе (так помечают отказ согласия): решать нечего.
        Assert.True(RestoreConfine.Allows(string.Empty, Paths(), isolated: true));
        Assert.True(RestoreConfine.Allows(null, Paths(), isolated: true));
    }

    /// <summary>
    /// У ОБЫЧНОГО ЗАПУСКА ЧЕЛОВЕКОМ поведение не меняется: там движок и Node возвращаются по своему
    /// месту, как и было решено (окно копий). Правило про изоляцию не имеет права трогать человека.
    /// </summary>
    [Fact]
    public void Человеку_правило_не_мешает()
    {
        Assert.True(RestoreConfine.Allows(@"C:\Program Files\nodejs", Paths(), isolated: false));
        Assert.True(RestoreConfine.Allows(@"C:\Users\Кто-то\AppData\Roaming\npm", Paths(), isolated: false));
        Assert.True(RestoreConfine.Allows(@"C:\Temp\dsh-confine-probe\engine\npm", Paths(), isolated: false));
    }
}
