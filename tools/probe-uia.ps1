param(
    [int]$ProcessId,
    [string]$Out = '',
    [string]$Title = '',
    [int]$TimeoutMs = 6000,
    [int]$MaxDepth = 40
)

# =====================================================================================
# РАМКА ЗАМЕРА UIA (этап 2.4.3) — что Windows рассказывает о НАШЕЙ таблице.
#
# Читает поддерево настоящего окна пробы (tools\TableViewProbe --live) через
# UI Automation: ControlType каждого узла, Name, IsContentElement, AutomationId,
# доступные образцы (Table/TableItem/Grid/Selection/Invoke) и — главное для Narrator —
# связь «шапка ↔ ячейка» (TableItemPattern.ColumnHeaderItems).
#
# Windows PowerShell 5.1 + UIAutomationClient, как в задании: UIA — системная часть,
# и мерить её надо тем же, чем её видит экранный диктор.
#
# Коды возврата: 0 — окно найдено и обойдено; 2 — негодные ключи; 3 — окно не найдено.
# =====================================================================================

$ErrorActionPreference = 'Stop'

try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
} catch {
    Write-Output ('UIA ОТКАЗ: не поднялись сборки UIAutomation: ' + $_.Exception.Message)
    exit 2
}

if ($ProcessId -le 0) {
    Write-Output 'UIA ОТКАЗ: нужен -Pid процесса с окном (ключ -Pid)'
    exit 2
}

$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]

function Find-ProbeWindow {
    param([int]$ProcessId, [string]$NamePart, [int]$Timeout)

    $deadline = (Get-Date).AddMilliseconds($Timeout)

    do {
        $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcessId)
        $byWindow = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ControlType::Window)
        $and = New-Object System.Windows.Automation.AndCondition($byPid, $byWindow)

        $found = $A::RootElement.FindFirst($Scope::Children, $and)
        if ($null -ne $found) { return $found }

        # Запасной путь: окно нашли по ЗАГОЛОВКУ, а не по процессу (так видно, если UIA
        # не отдаёт ProcessId для окна вне экрана). Ищем среди верхних окон.
        if ($NamePart -ne '') {
            $children = $A::RootElement.FindAll($Scope::Children, [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($child in $children) {
                try {
                    if ($child.Current.Name -like ('*' + $NamePart + '*')) { return $child }
                } catch { }
            }
        }

        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    return $null
}

function Get-Prop {
    param($Element, [string]$Name)
    try {
        $value = $Element.Current.$Name
        if ($null -eq $value) { return '' }
        return $value
    } catch {
        return ''
    }
}

function Get-Names {
    param($Items)
    $names = @()
    foreach ($item in $Items) {
        try { $names += [string]$item.Current.Name } catch { }
    }
    return $names
}

function Get-Node {
    param($Element, [int]$Depth)

    $node = New-Object psobject
    $node | Add-Member -MemberType NoteProperty -Name Depth -Value $Depth
    $node | Add-Member -MemberType NoteProperty -Name ControlType -Value (([string]$Element.Current.ControlType.ProgrammaticName) -replace '^ControlType\.', '')
    $node | Add-Member -MemberType NoteProperty -Name Name -Value (Get-Prop $Element 'Name')
    $node | Add-Member -MemberType NoteProperty -Name AutomationId -Value (Get-Prop $Element 'AutomationId')
    $node | Add-Member -MemberType NoteProperty -Name ClassName -Value (Get-Prop $Element 'ClassName')
    $node | Add-Member -MemberType NoteProperty -Name IsContentElement -Value (Get-Prop $Element 'IsContentElement')
    $node | Add-Member -MemberType NoteProperty -Name IsControlElement -Value (Get-Prop $Element 'IsControlElement')
    $node | Add-Member -MemberType NoteProperty -Name IsEnabled -Value (Get-Prop $Element 'IsEnabled')
    $node | Add-Member -MemberType NoteProperty -Name IsOffscreen -Value (Get-Prop $Element 'IsOffscreen')
    $node | Add-Member -MemberType NoteProperty -Name HasKeyboardFocus -Value (Get-Prop $Element 'HasKeyboardFocus')

    $rect = $null
    try { $rect = $Element.Current.BoundingRectangle } catch { }
    if ($null -ne $rect) {
        $node | Add-Member -MemberType NoteProperty -Name X -Value ([math]::Round($rect.X, 1))
        $node | Add-Member -MemberType NoteProperty -Name Y -Value ([math]::Round($rect.Y, 1))
        $node | Add-Member -MemberType NoteProperty -Name W -Value ([math]::Round($rect.Width, 1))
        $node | Add-Member -MemberType NoteProperty -Name H -Value ([math]::Round($rect.Height, 1))
    }

    $patterns = @()
    $extra = @{}

    $probe = @(
        @{ Name = 'Table'; Pattern = [System.Windows.Automation.TablePattern]::Pattern },
        @{ Name = 'TableItem'; Pattern = [System.Windows.Automation.TableItemPattern]::Pattern },
        @{ Name = 'Grid'; Pattern = [System.Windows.Automation.GridPattern]::Pattern },
        @{ Name = 'GridItem'; Pattern = [System.Windows.Automation.GridItemPattern]::Pattern },
        @{ Name = 'Selection'; Pattern = [System.Windows.Automation.SelectionPattern]::Pattern },
        @{ Name = 'SelectionItem'; Pattern = [System.Windows.Automation.SelectionItemPattern]::Pattern },
        @{ Name = 'Invoke'; Pattern = [System.Windows.Automation.InvokePattern]::Pattern },
        @{ Name = 'Toggle'; Pattern = [System.Windows.Automation.TogglePattern]::Pattern },
        @{ Name = 'Value'; Pattern = [System.Windows.Automation.ValuePattern]::Pattern }
    )

    foreach ($candidate in $probe) {
        $object = $null
        $has = $false
        try { $has = $Element.TryGetCurrentPattern($candidate.Pattern, [ref]$object) } catch { $has = $false }
        if (-not $has) { continue }

        $patterns += $candidate.Name

        if ($candidate.Name -eq 'Table') {
            try { $extra['Rows'] = $object.Current.RowCount; $extra['Columns'] = $object.Current.ColumnCount } catch { }
            try { $extra['ColumnHeaders'] = (Get-Names $object.Current.GetColumnHeaders()) } catch { }
        }

        if ($candidate.Name -eq 'Grid') {
            try { $extra['Rows'] = $object.Current.RowCount; $extra['Columns'] = $object.Current.ColumnCount } catch { }
        }

        if ($candidate.Name -eq 'TableItem') {
            try { $extra['ColumnHeaderItems'] = (Get-Names $object.Current.ColumnHeaderItems) } catch { }
            try { $extra['RowHeaderItems'] = (Get-Names $object.Current.RowHeaderItems) } catch { }
        }

        if ($candidate.Name -eq 'GridItem') {
            try { $extra['Column'] = $object.Current.Column; $extra['Row'] = $object.Current.Row } catch { }
        }

        if ($candidate.Name -eq 'SelectionItem') {
            try { $extra['IsSelected'] = $object.Current.IsSelected } catch { }
        }

        if ($candidate.Name -eq 'Toggle') {
            try { $extra['ToggleState'] = [string]$object.Current.ToggleState } catch { }
        }

        if ($candidate.Name -eq 'Value') {
            try { $extra['Value'] = [string]$object.Current.Value } catch { }
        }
    }

    $node | Add-Member -MemberType NoteProperty -Name Patterns -Value $patterns
    foreach ($key in $extra.Keys) {
        $node | Add-Member -MemberType NoteProperty -Name $key -Value $extra[$key]
    }

    return $node
}

$window = Find-ProbeWindow -ProcessId $ProcessId -NamePart $Title -Timeout $TimeoutMs

if ($null -eq $window) {
    Write-Output ('UIA НЕ НАЙДЕНО: окно процесса ' + $ProcessId + ' за ' + $TimeoutMs + ' мс')
    exit 3
}

$nodes = New-Object System.Collections.Generic.List[object]
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker

function Walk-Tree {
    param($Element, [int]$Depth)

    $nodes.Add((Get-Node -Element $Element -Depth $Depth))

    if ($Depth -ge $MaxDepth) { return }

    try { $child = $walker.GetFirstChild($Element) } catch { $child = $null }

    while ($null -ne $child) {
        Walk-Tree -Element $child -Depth ($Depth + 1)
        try { $child = $walker.GetNextSibling($child) } catch { $child = $null }
    }
}

Walk-Tree -Element $window -Depth 0

$all = $nodes.ToArray()

# КОМПАКТНАЯ ТАБЛИЦА: только то, что отвечает на вопрос задания, — роль, имя, содержимое,
# образцы и связь шапки с ячейкой. Остальное остаётся в JSON.
$interesting = @('Table', 'TableItem', 'Grid', 'GridItem', 'DataItem', 'List', 'ListItem', 'Header', 'HeaderItem', 'Text', 'Custom')
$lines = @()
$lines += ('UIA окно="' + (Get-Prop $window 'Name') + '" pid=' + $ProcessId)
$lines += 'ГЛУБ|РOЛЬ|ИМЯ|СОДЕРЖИМОЕ|ОБРАЗЦЫ|ШАПКА/КОЛОНКА|X,Y|WxH'

foreach ($node in $all) {
    $role = [string]$node.ControlType
    if (($interesting -notcontains $role) -and ($node.Patterns.Count -eq 0)) { continue }

    $name = [string]$node.Name
    if ($name.Length -gt 46) { $name = $name.Substring(0, 46) + '…' }
    $name = $name -replace '\|', '/'
    $content = '' + $node.IsContentElement
    $patterns = ''
    if ($node.Patterns.Count -gt 0) { $patterns = [string]::Join('+', $node.Patterns) }

    $link = ''
    if ($null -ne $node.PSObject.Properties['ColumnHeaderItems']) {
        $link = 'шапки=' + [string]::Join(';', ([string[]]$node.ColumnHeaderItems))
    }
    if ($null -ne $node.PSObject.Properties['Column']) {
        $link = $link + ' кол=' + $node.Column + ' ряд=' + $node.Row
    }

    $size = '(' + $node.X + ',' + $node.Y + ') ' + $node.W + 'x' + $node.H
    $lines += ('' + $node.Depth + '|' + $role + '|' + $name + '|' + $content + '|' + $patterns + '|' + $link + '|' + $size)
}

# ЧИСЛА ПО РОЛЯМ — то, что сравнивается между «сейчас» и «прототипом».
$byType = @{}
foreach ($node in $all) {
    $role = [string]$node.ControlType
    if (-not $byType.ContainsKey($role)) { $byType[$role] = 0 }
    $byType[$role] = $byType[$role] + 1
}

$lines += ''
$lines += 'ИТОГ узлов=' + $all.Count

foreach ($key in ($byType.Keys | Sort-Object)) {
    $lines += ('  ' + $key + ' = ' + $byType[$key])
}

$tableCount = 0
$tableItemCount = 0
$withHeaders = 0
foreach ($node in $all) {
    if ([string]$node.ControlType -eq 'Table') { $tableCount = $tableCount + 1 }
    if ([string]$node.ControlType -eq 'TableItem') { $tableItemCount = $tableItemCount + 1 }
    if ($null -ne $node.PSObject.Properties['ColumnHeaderItems']) {
        if ($node.ColumnHeaderItems.Count -gt 0) { $withHeaders = $withHeaders + 1 }
    }
}

$lines += ('СВЯЗЬ шапка↔ячейка: Table=' + $tableCount + ' TableItem=' + $tableItemCount + ' с непустыми шапками=' + $withHeaders)

foreach ($line in $lines) { Write-Output $line }

if ($Out -ne '') {
    $payload = New-Object psobject
    $payload | Add-Member -MemberType NoteProperty -Name Pid -Value $ProcessId
    $payload | Add-Member -MemberType NoteProperty -Name Window -Value (Get-Prop $window 'Name')
    $payload | Add-Member -MemberType NoteProperty -Name Counts -Value $byType
    $payload | Add-Member -MemberType NoteProperty -Name Nodes -Value $all

    $json = $payload | ConvertTo-Json -Depth 8

    # .json — БЕЗ BOM (правило репозитория); каталог создаём.
    $folder = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Out))
    if ($folder -ne '' -and -not [System.IO.Directory]::Exists($folder)) {
        [System.IO.Directory]::CreateDirectory($folder) | Out-Null
    }

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Out), $json, $utf8)
}

exit 0
