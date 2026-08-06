<div dir="rtl">

# סקירת פרויקט הבדיקות — `PlayerService.Tests`

> כל הבדיקות נכתבו כבדיקות אינטגרציה אמיתיות — ללא Mocks על Grains, ומורצות מול סביבת Orleans אמיתית ב-Process. גישה זו מוכיחה ש**קוד הייצור (Production)** עובד, ולא רק שמחלקה כלשהי מחזירה ערך נכון בבידוד.

---

## מבנה כללי

```text
tests/
└── PlayerService.Tests/
    ├── Fixtures (תשתית)
    │   ├── ClusterFixture.cs
    │   ├── ManualClockClusterFixture.cs
    │   ├── LeaderboardClusterFixture.cs
    │   └── ApiFixture.cs
    │
    └── Test Classes (מחלקות בדיקה)
        ├── TopologyTests.cs         ← Phase 1
        ├── SessionTests.cs          ← Phase 2
        ├── ScoreTests.cs            ← Phase 3
        ├── GiftTests.cs             ← Phase 4
        ├── LeaderboardTests.cs      ← Phase 5
        └── ApiContractTests.cs      ← HTTP Contract
```

---

## Fixtures — תשתית הבדיקות

ה-Fixtures הם המנוע מאחורי הבדיקות. כל Fixture מרים קלאסטר Orleans אמיתי ב-Process לפני ריצת הבדיקות, ומוריד אותו לאחר מכן.

---

### `ClusterFixture.cs`

**אחריות:** תשתית כללית עבור רוב הבדיקות — קלאסטר Orleans עם **שני Silos** ב-Process.

| פרט | ערך |
|-----|-----|
| מספר Silos | 2 (במכוון!) |
| Collection Name | `"orleans-cluster"` |
| משמש עבור | `TopologyTests`, `SessionTests`, `ScoreTests`, `GiftTests` |

**למה שני Silos?**
עם Silo אחד, לא ניתן להבחין בין טענות כגון "Single Activation Cluster-Wide" לבין שימוש ב-Dictionary מקומי. שימוש בשני Silos מאלץ את Orleans לנהל Grain Activation בין מכונות שונות, בדיוק כמו בסביבת הייצור (Production).

**מה כלול:**
- `Cluster` — ה-`InProcessTestCluster` עצמו.
- `Gifts` — מופע אמיתי של `GiftService` שנבנה מהקונטיינר של ה-Silo, מכיוון שזו התצורה בסביבת הייצור: ה-API Pod מתפקד כ-Silo בעצמו.

**תת-מחלקות בקובץ:**
- `StaticOptionsMonitor<T>` — עוטף Options סטטיות כדי לדמות `IOptionsMonitor` ללא צורך ב-DI מלא.
- `TestMetrics` — יוצר `PlayerServiceMetrics` אמיתיים ללא Collector, כך שקוד ה-Instrumentation נמצא על הנתיב הנבדק בפועל.

---

### `ManualClockClusterFixture.cs`

**אחריות:** קלאסטר שני עם **שעון מניפולטיבי** — מאפשר לקדם את הזמן ידנית במקום "לישון".

| פרט | ערך |
|-----|-----|
| מספר Silos | 2 |
| Collection Name | `"orleans-cluster-manual-clock"` |
| משמש עבור | `SessionExpiryTests` |

**מה כלול:**
- `ManualTimeProvider` — מחלקה פנימית שמאפשרת `Advance(TimeSpan)`, כלומר "קפוץ קדימה ב-3 דקות" מבלי לחכות בפועל.
- `Clock` — ה-`ManualTimeProvider` חשוף ישירות לבדיקות.

**למה קלאסטר נפרד?**
שעון קפוא (סטטי) לא אמור להשפיע על בדיקות שמסתמכות על זמן אמיתי. אם `ClusterFixture` היה משתף שעון זה, בדיקות לא קשורות היו מתנהגות בצורה לא צפויה.

---

### `LeaderboardClusterFixture.cs`

**אחריות:** קלאסטר ייעודי ל-Leaderboard, עם **`LeaderboardCache` אחד לכל Silo** — מדמה את ארכיטקטורת ה-Push האמיתית.

| פרט | ערך |
|-----|-----|
| מספר Silos | 2 |
| Collection Name | `"orleans-cluster-leaderboard"` |
| משמש עבור | `LeaderboardTests` |

**מה כלול:**
- `Pods` — רשימה של `LeaderboardCache`, אחד לכל Silo (ממש כמו Pod API אמיתי).
- `Gifts` — שירות מתנות (משום שמתנות מזינות את אותו Stream כמו ה-Score).

**למה קלאסטר נפרד?**
ה-`LeaderboardGrain` הוא Singleton ברמת הקלאסטר המחזיק את **כל** השחקנים שאי פעם קיבלו ניקוד. שיתוף קלאסטר עם שאר הבדיקות היה יוצר תלות ביניהן — שחקנים מבדיקות אחרות היו "זולגים" ללוח הניקוד שנבדק.

---

### `ApiFixture.cs`

**אחריות:** מרים את **ה-API האמיתי** (`Program.cs`) מאחורי `HttpClient` — ביצוע בדיקות HTTP end-to-end.

| פרט | ערך |
|-----|-----|
| מספר Silos | 1 (במכוון) |
| Collection Name | `"player-service-api"` |
| משמש עבור | `ApiContractTests` |

**למה Silo אחד בלבד?**
ההבטחות (Guarantees) ברמת הקלאסטר כבר נבדקות ב-`ClusterFixture` (באמצעות שני Silos). שכבת ה-HTTP מאפשרת לנו לבדוק את ה-**Request Pipeline** בלבד: אימות (Auth), הרשאות (Ownership), אימות נתונים (Model Validation), ומיפוי תוצאות הדומיין לקודי סטטוס ב-HTTP.

**מה כלול:**
- `Client` — `HttpClient` אמיתי לביצוע שאילתות HTTP.
- פורטים ייחודיים (21111, 21112) כדי למנוע התנגשות עם Silo שרץ על המכונה של המפתח.
- השבתת Observability Exporters בסביבת Testing (כדי למנוע כתיבה מיותרת ל-Console).

---

## מחלקות הבדיקה

---

### `TopologyTests.cs` — Phase 1

**שאלת הבסיס:** האם התצורה והחיבורים של Orleans עובדים?

| בדיקה | מטרתה |
|-----|------------|
| `Cluster_runs_two_silos` | מוודא שהקלאסטר מכיל בדיוק 2 Silos |
| `Player_grain_activates_with_the_seeded_balance` | מוודא ש-PlayerGrain מופעל עם יתרת פתיחה של 1000, וש-Transaction Manager, TransactionStore Provider וסריאליזציה — כולם מוגדרים ומקושרים היטב |
| `Leaderboard_grain_activates_and_subscribes_to_its_stream` | מוודא ש-LeaderboardGrain מופעל ומתחיל להאזין ל-Stream (מוכיח ש-Memory Stream Provider ו-PubSubStore מוגדרים) |
| `Login_binds_a_session_that_the_player_grain_validates` | זרימת Login מקצה לקצה: DeviceGrain → PlayerGrain, פורמט ה-Token תקין, וה-Validation עובד כראוי |

> **מטרה:** בדיקות "Smoke" שייכשלו מיידית אם תצורת Orleans חסרה — עוד לפני שמגיעים לבדיקת הלוגיקה העסקית.

---

### `SessionTests.cs` — Phase 2

**שאלת הבסיס:** האם שער ה-Login ומדיניות ה-Supersede עובדים ברמת הקלאסטר?

| בדיקה | מטרתה |
|-----|------------|
| `Concurrent_logins_on_one_device_grant_exactly_one_session` | 8 ניסיונות התחברות (Login) במקביל מאותו Device — בדיוק אחד מצליח, ו-7 נדחים עם השגיאה `DeviceSessionActive` |
| `A_second_device_supersedes_the_first_players_token` | Device שני מקבל עדיפות ודוחק (Supersedes) את זה שלפניו — ה-Token הראשון מפסיק לעבוד מיידית |
| `A_superseded_device_can_log_in_again_immediately` | Device שהוחלף (Superseded) יצליח להתחבר מחדש מיד (מכיוון שקיבל Release) |
| `A_device_holding_a_live_session_rejects_a_different_player` | Device בעל Session פעיל ידחה שחקן (Player) שונה |

> **מה נבדק ברמת הקלאסטר:** ההבטחה (Guarantee) ל-Single Activation של Orleans — ולא מנגנון נעילה מקומי (Local Lock) — היא זו שמסדרת את ניסיונות ההתחברות ומבטיחה ביצוע תקין.

---

### `SessionExpiryTests.cs` — Phase 2 (Expiry)

**שאלת הבסיס:** האם פקיעת ה-Session עובדת נכון עם Sliding TTL של 3 דקות?

| בדיקה | מטרתה |
|-----|------------|
| `An_active_session_slides_and_outlives_the_ttl` | Session שמקבל בקשה כל 90 שניות ימשיך להתקיים ולשרוד יותר מ-3 דקות בסך הכל |
| `A_silent_session_expires_and_frees_its_device` | Session שאינו פעיל יפקע וישחרר את ה-Device |
| `Devices_superseding_each_other_simultaneously_do_not_deadlock` | שני Devices מחליפים שחקנים בו זמנית — ולא נתקעים במצב קיפאון (Deadlock), בזכות התכונה `[AlwaysInterleave]` על מתודת ה-`ReleaseAsync` |

> **טכניקה:** השימוש ב-`ManualTimeProvider` מאפשר לקפוץ קדימה ב-3 דקות באופן יזום — כך הבדיקות מדויקות ואינן תלויות ב-`Thread.Sleep`.

---

### `ScoreTests.cs` — Phase 3

**שאלת הבסיס:** האם עדכוני הניקוד אטומיים ואידמפוטנטיים (Idempotent)?

| בדיקה | מטרתה |
|-----|------------|
| `N_parallel_score_posts_sum_exactly` | 50 עדכוני ניקוד במקביל — הסכום הסופי מדויק ואין אובדן של עדכונים |
| `Duplicate_requestId_fired_in_parallel_applies_once` | 100 בקשות במקביל המשתמשות באותו `requestId` — הפעולה מבוצעת פעם אחת בלבד, וכל הבקשות מקבלות בחזרה את אותה התוצאה |

> **המנגנון הנבדק:** שימוש ב-`IdempotencyLedger` בתוך ה-`PlayerGrain` בשילוב Transactional State מונע מצבים של TOCTOU (Time-of-Check to Time-of-Use).

---

### `GiftTests.cs` — Phase 4

**שאלת הבסיס:** האם מנגנון המתנות פועל כ-Distributed Transaction תקין על שני Silos שונים?

| בדיקה | מטרתה |
|-----|------------|
| `A_gift_moves_points_and_counts_on_both_sides` | מתנה מעבירה נקודות ומעדכנת את המונים GiftsSent ו-GiftsReceived בשני הצדדים |
| `Many_concurrent_gifts_across_random_pairs_conserve_points` | 60 מתנות במקביל בין 8 שחקנים — סך הנקודות הכולל במערכת נשמר ללא איבוד מידע |
| `Gifts_in_both_directions_between_one_pair_do_not_deadlock` | שליחת 30 מתנות במקביל (15 פעמים p1→p2 ו-15 פעמים p2→p1) — לא יוצרת Deadlock, משום שה-Call Graph בנוי בצורת עץ |
| `A_replayed_requestId_returns_the_original_outcome_and_moves_nothing` | שימוש חוזר באותו `requestId` — מחזיר את אותה התוצאה ללא העברה חוזרת של נקודות |
| `A_gift_replayed_after_the_recipient_went_offline_still_returns_the_original` | בקשת Replay תקינה מתקבלת גם אם השחקן המקבל התנתק (Offline) |
| `A_gift_to_an_offline_player_is_rejected_and_moves_no_points` | שליחת מתנה לשחקן Offline — נדחית, ללא כל שינוי במאזן הנקודות |
| `A_replayed_rejected_requestId_returns_the_original_rejection` | דחייה (Rejection) גם היא יציבה לניסיונות חוזרים (Replay-stable) |
| `A_gift_beyond_the_balance_is_rejected_and_never_goes_negative` | שליחת מתנה הגדולה מהיתרה — נדחית, והיתרה לא הופכת לשלילית |
| `Two_simultaneous_gifts_that_together_overdraw_let_only_one_through` | שתי מתנות שביחד חורגות מהיתרה — בדיוק אחת מהן עוברת בהצלחה |
| `A_self_gift_is_rejected_without_opening_a_transaction` | שליחת מתנה של שחקן לעצמו — נדחית עוד לפני פתיחת Transaction |
| `A_gift_to_a_player_who_never_logged_in_is_rejected_as_unknown` | שליחת מתנה לשחקן לא קיים מחזירה שגיאת `UnknownRecipient` (שתורגמה ל-404 ב-API) |

> **הרעיון המרכזי:** השולח (Sender) והמקבל (Recipient) יכולים להימצא על Silos שונים — זהו בדיוק המצב בו מנגנון נעילה כפולה (Double-Lock) קלאסי נכשל. שימוש ב-Orleans Transactions יחד עם מבנה היררכי (עץ קריאות) מהווה את הפתרון הנכון והבטוח.

---

### `LeaderboardTests.cs` — Phase 5

**שאלת הבסיס:** האם ה-Leaderboard מתכנס נכון ומודל ה-Push מתפקד כראוי?

| בדיקה | מטרתה |
|-----|------------|
| `The_board_converges_on_the_true_scores_after_a_burst` | לאחר כמות גדולה של עדכוני ניקוד ברצף (Burst) — הלוח מתכנס לבסוף לציונים הנכונים |
| `A_gift_moves_both_players_on_the_board` | מתנה מביאה לעדכון של שני השחקנים בלוח הניקוד (השולח יורד והמקבל עולה) |
| `Both_pods_converge_on_the_same_snapshot` | שני Pods שונים מגיעים לאותו Snapshot בדיוק — במקום שכל אחד יחשב עותק נפרד משלו |
| `A_pod_joining_late_primes_itself_from_the_grain` | Pod שמצטרף באיחור אינו נשאר ריק — אלא מבצע Pull מיידי של הנתונים מה-Grain |

> **טכניקה:** הבדיקות מבצעות **Polling** בטווח של עד 20 שניות במקום שימוש ב-`Thread.Sleep` — מכיוון שה-Leaderboard הוא א-סינכרוני (Eventually Consistent). שימוש ב-Polling הוא הדרך הנכונה לוודא ש"המערכת מתכנסת למצב הרצוי בטווח זמן מוגדר" ולא להסתמך על השערה שהיא תהיה "נכונה ברגע מסוים".

---

### `ApiContractTests.cs` — HTTP Contract

**שאלת הבסיס:** האם הלקוח יכול לדעת בבירור *מה התרחש* על בסיס קוד הסטטוס?

| בדיקה | מטרתה |
|-----|------------|
| `Login_returns_a_token_and_a_seeded_player` | פעולת Login מחזירה Token יחד עם שחקן המכיל יתרת פתיחה של 1000 נקודות |
| `Simultaneous_logins_on_one_device_yield_one_200_and_the_rest_409` | מתוך 8 בקשות Login במקביל — בדיוק בקשה אחת תחזיר 200, ו-7 הבקשות הנותרות יחזירו 409 |
| `A_request_without_a_token_is_401` | בקשה ללא Token תחזיר `401 Unauthorized` |
| `A_token_cannot_reach_another_players_resource` | שימוש ב-Token של שחקן A לביצוע פעולה עבור שחקן B יחזיר `403 Forbidden` |
| `A_superseded_token_is_401` | ניסיון לשימוש ב-Token שהוחלף (Superseded) יחזיר `401 Unauthorized` |
| `Duplicate_score_posts_return_identical_responses_and_apply_once` | 20 בקשות כפולות (Duplicates) — כולן יחזירו 200 עם אותו הניקוד בדיוק, והפעולה תבוצע בפועל רק פעם אחת |
| `A_non_positive_score_is_400_before_any_grain_is_touched` | שליחת 0 נקודות תחזיר `400 Bad Request` עוד לפני פנייה כלשהי ל-Grain |
| `The_gift_endpoint_maps_every_outcome_onto_its_own_status` | כל תוצאה אפשרית של פעולת שליחת מתנה ממופה לקוד סטטוס HTTP מתאים בצורה ייחודית |
| `A_replayed_gift_returns_the_original_body_marked_replayed` | פעולת Replay תחזיר סטטוס 200 יחד עם השדה `replayed: true` |
| `Concurrent_gifts_over_http_conserve_points` | 30 מתנות נשלחות במקביל בין 6 שחקנים — נבדקת הבטחת שמירת הנקודות הכוללת במערכת |
| `The_leaderboard_is_readable_and_reports_its_own_age` | ה-Leaderboard מחזיר דירוגים (Ranks) וחותמת זמן (`computedAt`) תקינים |

> **ההבדל מבדיקות אחרות:** כאן אנו בודקים **מה הלקוח מקבל בפועל** — HTTP Status Codes ומבנה ה-Body. זוהי הרמה שעליה מסתמך לקוח בסביבת רשת לא-אמינה כדי להחליט האם עליו לנסות ולבצע את הבקשה שוב.

---

## בדיקות קצה-לקצה (E2E Tests) — `PlayerService.E2ETests`

בנוסף לטסטים שרצים מול קלאסטר באותו התהליך (In-Process), הפרויקט כולל סט בדיקות E2E שמיועד לרוץ מול **סביבה חיה (Live Service)** המאוחסנת בנפרד (או ב-Container). הבדיקות פונות לשירות אך ורק דרך שכבת ה-HTTP, בדיוק כמו לקוח משחק אמיתי.

### `E2EEnvironment.cs` & `LiveServiceFixture.cs`
**אחריות:** ניהול הסביבה מולה רצות הבדיקות.
- הבדיקות בודקות לפני הריצה האם השירות זמין דרך Endpoint ה-`/health`. אם לא – הן מסומנות ככאלו ש"דולגו" (Skipped) במקום להיכשל, כדי למנוע מצב בו `dotnet test` נכשל רק כי השרת לא מורץ ברקע.
- הגישה ל-API מתבצעת באמצעות מחלקה ייעודית `PlayerServiceClient` שעוטפת את `HttpClient`.

### מחלקות הבדיקה ב-E2E:

| מחלקה | תיאור ומטרה |
|-------|-------------|
| `SessionTests.cs` | בוחנת את ניהול ה-Sessions וה-Sliding Expiration בסביבה חיה דרך הרשת. |
| `ScoreConcurrencyTests.cs` | שולחת בקשות לעדכון ניקוד במקביל (Concurrency) כדי לוודא שאין איבוד נתונים או שגיאות תחת עומס. |
| `GiftConcurrencyTests.cs` | מדמה תרחישי תעבורה כבדים של שליחת מתנות (כולל שליחה מעגלית) כדי להוכיח שהמערכת שומרת על עקביות, שהיתרות לא יורדות מתחת לאפס, ומונעת Deadlocks באופן מושלם תחת עומס. |
| `OfflineGiftTests.cs` | בודקת תרחישי קצה ייחודיים שבהם שחקן מתנתק (Offline) וממתינה בפועל לזמן ה-TTL כדי לוודא שפעולות כגון Replay ודחיות מתנה מתנהגות כראוי. |
| `LeaderboardTests.cs` | בודקת את התכנסות לוח הניקוד על ידי יצירת עומס (Burst) של מתנות ועדכוני ניקוד, ואז ביצוע Polling ללוח כדי לוודא שמודל ה-Push בסביבה מבוזרת מציג תוצאות מדויקות ותקינות. |

---

## מפת תלויות

```text
                    ┌─────────────────────────┐
                    │      Test Classes       │
                    └──────────┬──────────────┘
                               │ uses
          ┌────────────────────┼────────────────────┐
          ▼                    ▼                    ▼
  ClusterFixture    ManualClockClusterFixture  LeaderboardClusterFixture
  (2 Silos)         (2 Silos + ManualClock)    (2 Silos + LeaderboardCache per Pod)
       │                    │                        │
       └────────────────────┴───────────────────────┘
                               │ all use
                    AddPlayerServiceOrleans()
                    (same production grain wiring)

                    ┌─────────────────────────┐
                    │       ApiFixture        │
                    │ (Program.cs, 1 Silo)    │
                    └─────────────────────────┘
                          used by ApiContractTests only
```

---

## עקרונות המנחים את כל הטסטים

1. **אין שימוש ב-Mocks עבור Grains** — אנו מריצים Grains אמיתיים בתוך ה-Process, ולכן השגיאות שמתקבלות הן שגיאות אמיתיות מתוך המערכת.
2. **שימוש בשני Silos תמיד** — עבודה עם Silo בודד לא מוכיחה את ההבטחות של Orleans, אלא רק מעידה על כך שמבנה נתונים מקומי (Local Dictionary) עובד (למעט שכבת ה-API שבה אנו בוחנים את ה-HTTP).
3. **Fixtures נפרדים לתרחישים נפרדים** — תרחישי שעון (Clock), לוח ניקוד (Leaderboard) ו-API מבודדים ולא חולקים את אותו קלאסטר, בכדי למנוע זליגת נתונים.
4. **שימוש ב-Polling במקום Sleep** — בבדיקות של מערכות Eventually Consistent אנו מבצעים Polling ברצף עד להגעה ל-Deadline מוגדר, ולא ממתינים (Sleep) זמן קצוב שעלול לגרור חוסר יציבות (Flakiness).
5. **שימוש בקוד ייצור (Production) אמיתי** — הרכיבים כגון `GiftService`, `LeaderboardCache`, `Metrics` וכו', הינם האובייקטים האמיתיים ולא Mock-ים (חיקויים).

</div>
