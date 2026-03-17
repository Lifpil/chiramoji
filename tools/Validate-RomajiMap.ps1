param(
    [string]$SourcePath = "Chiramoji/Services/KeyloggerService.cs"
)

$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)
$text = [System.IO.File]::ReadAllText((Resolve-Path $SourcePath), $utf8)
$matches = [regex]::Matches($text, '\{"((?:\\.|[^"\\])*)","((?:\\.|[^"\\])*)"\}')
$map = @{}
foreach ($match in $matches) {
    $key = $match.Groups[1].Value.Replace('\\"', '"').Replace('\\\\', '\\')
    $value = $match.Groups[2].Value.Replace('\\"', '"').Replace('\\\\', '\\')
    $map[$key] = $value
}

$expected = [ordered]@{
    a='あ'; i='い'; u='う'; e='え'; o='お'; yi='い'; ye='いぇ';
    ka='か'; ca='か'; ki='き'; ku='く'; cu='く'; qu='く'; ke='け'; ko='こ'; co='こ';
    sa='さ'; si='し'; shi='し'; ci='し'; su='す'; se='せ'; ce='せ'; so='そ';
    ta='た'; ti='ち'; chi='ち'; tu='つ'; tsu='つ'; te='て'; to='と';
    na='な'; ni='に'; nu='ぬ'; ne='ね'; no='の';
    ha='は'; hi='ひ'; hu='ふ'; fu='ふ'; he='へ'; ho='ほ';
    ma='ま'; mi='み'; mu='む'; me='め'; mo='も';
    ya='や'; yu='ゆ'; yo='よ';
    ra='ら'; ri='り'; ru='る'; re='れ'; ro='ろ';
    wa='わ'; wi='うぃ'; wu='う'; we='うぇ'; wo='を';
    nn='ん'; "n'"='ん';
    ga='が'; gi='ぎ'; gu='ぐ'; ge='げ'; go='ご';
    za='ざ'; zi='じ'; ji='じ'; zu='ず'; ze='ぜ'; zo='ぞ';
    da='だ'; di='ぢ'; du='づ'; de='で'; do='ど';
    ba='ば'; bi='び'; bu='ぶ'; be='べ'; bo='ぼ';
    pa='ぱ'; pi='ぴ'; pu='ぷ'; pe='ぺ'; po='ぽ';
    xa='ぁ'; la='ぁ'; xi='ぃ'; li='ぃ'; xu='ぅ'; lu='ぅ'; xe='ぇ'; le='ぇ'; xo='ぉ'; lo='ぉ';
    xka='ヵ'; lka='ヵ'; xke='ヶ'; lke='ヶ'; xtu='っ'; ltu='っ'; ltsu='っ';
    xya='ゃ'; lya='ゃ'; xyi='ぃ'; lyi='ぃ'; xyu='ゅ'; lyu='ゅ'; xye='ぇ'; lye='ぇ'; xyo='ょ'; lyo='ょ'; xwa='ゎ'; lwa='ゎ';
    va='ヴぁ'; vi='ヴぃ'; vyi='ヴぃ'; vu='ヴ'; ve='ヴぇ'; vye='ヴぇ'; vo='ヴぉ';
    kya='きゃ'; kyi='きぃ'; kyu='きゅ'; kye='きぇ'; kyo='きょ';
    sya='しゃ'; sha='しゃ'; syi='しぃ'; syu='しゅ'; shu='しゅ'; sye='しぇ'; she='しぇ'; syo='しょ'; sho='しょ';
    cya='ちゃ'; tya='ちゃ'; cha='ちゃ'; cyi='ちぃ'; tyi='ちぃ'; cyu='ちゅ'; tyu='ちゅ'; chu='ちゅ'; cye='ちぇ'; tye='ちぇ'; che='ちぇ'; cyo='ちょ'; tyo='ちょ'; cho='ちょ';
    nya='にゃ'; nyi='にぃ'; nyu='にゅ'; nye='にぇ'; nyo='にょ';
    hya='ひゃ'; hyi='ひぃ'; hyu='ひゅ'; hye='ひぇ'; hyo='ひょ';
    mya='みゃ'; myi='みぃ'; myu='みゅ'; mye='みぇ'; myo='みょ';
    tha='てゃ'; thi='てぃ'; thu='てゅ'; the='てぇ'; tho='てょ';
    rya='りゃ'; ryi='りぃ'; ryu='りゅ'; rye='りぇ'; ryo='りょ';
    dha='でゃ'; dhi='でぃ'; dhu='でゅ'; dhe='でぇ'; dho='でょ';
    fya='ふゃ'; fyi='ふぃ'; fyu='ふゅ'; fye='ふぇ'; fyo='ふょ';
    gya='ぎゃ'; gyi='ぎぃ'; gyu='ぎゅ'; gye='ぎぇ'; gyo='ぎょ';
    ja='じゃ'; jya='じゃ'; zya='じゃ'; jyi='じぃ'; zyi='じぃ'; ju='じゅ'; jyu='じゅ'; zyu='じゅ'; je='じぇ'; jye='じぇ'; zye='じぇ'; jo='じょ'; jyo='じょ'; zyo='じょ';
    dya='ぢゃ'; dyi='ぢぃ'; dyu='ぢゅ'; dye='ぢぇ'; dyo='ぢょ';
    bya='びゃ'; byi='びぃ'; byu='びゅ'; bye='びぇ'; byo='びょ';
    pya='ぴゃ'; pyi='ぴぃ'; pyu='ぴゅ'; pye='ぴぇ'; pyo='ぴょ';
    fa='ふぁ'; fi='ふぃ'; fe='ふぇ'; fo='ふぉ';
    qa='くぁ'; qi='くぃ'; qyi='くぃ'; qe='くぇ'; qye='くぇ'; qo='くぉ';
    wha='うぁ'; whi='うぃ'; whu='う'; whe='うぇ'; who='うぉ';
    qya='くゃ'; qyu='くゅ'; qyo='くょ';
    vya='ヴゃ'; vyu='ヴゅ'; vyo='ヴょ';
    tsa='つぁ'; tsi='つぃ'; tse='つぇ'; tso='つぉ'
}

function Convert-Romaji([string]$romajiInput, $romajiMap) {
    $pending = ''
    $confirmed = ''

    foreach ($char in $romajiInput.ToLowerInvariant().ToCharArray()) {
        if ($pending -eq 'n' -and $char -eq "'") {
            $confirmed += 'ん'
            $pending = ''
            continue
        }

        if ($pending -eq 'n' -and 'aeiouyn'.IndexOf($char) -lt 0) {
            $confirmed += 'ん'
            $pending = ''
        }

        $pending += $char
        $progress = $true
        while ($progress -and $pending.Length -gt 0) {
            $progress = $false

            if ($pending.Length -ge 2 -and $pending[0] -ne 'n' -and $pending[0] -eq $pending[1]) {
                $confirmed += 'っ'
                $pending = $pending.Substring(1)
                $progress = $true
                continue
            }

            $maxLen = [Math]::Min($pending.Length, 4)
            for ($len = $maxLen; $len -ge 1; $len--) {
                $key = $pending.Substring(0, $len)
                if ($romajiMap.ContainsKey($key)) {
                    $confirmed += $romajiMap[$key]
                    $pending = $pending.Substring($len)
                    $progress = $true
                    break
                }
            }

            if (-not $progress) {
                $possiblePrefix = $false
                foreach ($candidate in $romajiMap.Keys) {
                    if ($candidate.StartsWith($pending)) {
                        $possiblePrefix = $true
                        break
                    }
                }

                if (-not $possiblePrefix) {
                    $confirmed += $pending.Substring(0, 1)
                    $pending = $pending.Substring(1)
                    $progress = $true
                }
            }
        }
    }

    $progress = $true
    while ($progress -and $pending.Length -gt 0) {
        $progress = $false

        if ($pending.Length -ge 2 -and $pending[0] -ne 'n' -and $pending[0] -eq $pending[1]) {
            $confirmed += 'っ'
            $pending = $pending.Substring(1)
            $progress = $true
            continue
        }

        $maxLen = [Math]::Min($pending.Length, 4)
        for ($len = $maxLen; $len -ge 1; $len--) {
            $key = $pending.Substring(0, $len)
            if ($romajiMap.ContainsKey($key)) {
                $confirmed += $romajiMap[$key]
                $pending = $pending.Substring($len)
                $progress = $true
                break
            }
        }

        if (-not $progress) {
            $possiblePrefix = $false
            foreach ($candidate in $romajiMap.Keys) {
                if ($candidate.StartsWith($pending)) {
                    $possiblePrefix = $true
                    break
                }
            }

            if (-not $possiblePrefix) {
                $confirmed += $pending.Substring(0, 1)
                $pending = $pending.Substring(1)
                $progress = $true
            }
        }
    }

    if ($pending -eq 'n') {
        $confirmed += 'ん'
    }
    elseif ($pending.Length -gt 0) {
        $confirmed += $pending
    }

    return $confirmed
}

$errors = New-Object System.Collections.Generic.List[string]
foreach ($entry in $expected.GetEnumerator()) {
    $actual = Convert-Romaji $entry.Key $map
    if ($actual -ne $entry.Value) {
        $errors.Add("$($entry.Key) => $actual (expected $($entry.Value))")
    }
}

$flushSamples = [ordered]@{
    n = 'ん'
    kan = 'かん'
    shin = 'しん'
    ten = 'てん'
    whin = 'うぃん'
    jen = 'じぇん'
}
foreach ($entry in $flushSamples.GetEnumerator()) {
    $actual = Convert-Romaji $entry.Key $map
    if ($actual -ne $entry.Value) {
        $errors.Add("flush:$($entry.Key) => $actual (expected $($entry.Value))")
    }
}

if ($errors.Count -gt 0) {
    Write-Host "Validation failed:" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Validation passed for $($expected.Count) romaji entries and $($flushSamples.Count) flush cases." -ForegroundColor Green