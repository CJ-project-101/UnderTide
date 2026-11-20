using UnityEngine;
using System.Collections;

[System.Serializable]
public class AurethPlatform
{
    public Transform spawnPoint;   // where Aureth lands when teleporting here
    public Transform leftEdge;     // this platform's own left boundary
    public Transform rightEdge;    // this platform's own right boundary
}

public class AurethBoss : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Animator anim;
    [SerializeField] private Health health;
    [SerializeField] private Rigidbody2D rb;
    [SerializeField] private Transform player;

    [Header("Patrol Edges (set automatically from the current platform)")]
    [SerializeField] private Transform leftEdge;
    [SerializeField] private Transform rightEdge;

    [Header("Patrol Movement")]
    [SerializeField] private float patrolSpeed = 1.5f;
    [SerializeField] private float patrolIdleDuration = 1f;
    private bool movingLeft;
    private float idleTimer;
    private Vector3 initScale;

    [Header("Chase Movement")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float magicRange = 5f;

    [Header("Attack Timing")]
    [SerializeField] private float decisionCooldown = 1.2f;
    [SerializeField] private float lightAttackDuration = 0.5f;
    [SerializeField] private float heavyAttackDuration = 0.9f;
    [SerializeField] private float castDuration = 0.6f;

    [Header("Magic")]
    [SerializeField] private Transform castPoint;
    [SerializeField] private GameObject heavyMagicPrefab;
    [SerializeField] private GameObject lightMagicPrefab;
    private bool pendingHeavyMagic;
    [SerializeField] private bool magicEnabled = true;

    [Header("Melee Hit Boxes")]
    [SerializeField] private LayerMask playerLayer;
    [SerializeField] private LayerMask wallLayer;
    [SerializeField] private float lightDamage = 1f;
    [SerializeField] private float heavyDamage = 2f;

    [Header("Light Attack Box")]
    [SerializeField] private Vector2 lightBoxOffset = new Vector2(1f, 0.2f);
    [SerializeField] private Vector2 lightBoxSize = new Vector2(1.2f, 1f);

    [Header("Heavy Attack Box")]
    [SerializeField] private Vector2 heavyBoxOffset = new Vector2(1.4f, 0.2f);
    [SerializeField] private Vector2 heavyBoxSize = new Vector2(1.8f, 1.4f);
    [Range(0f, 1f)]
    [SerializeField] private float heavyChanceInMelee = 0f;   // 0 = always light attack in the magenta box

    [Header("Stamina")]
    [SerializeField] private float maxStamina = 100f;
    private float currentStamina;

    [Header("Stamina Costs")]
    [SerializeField] private float heavyAttackStaminaCost = 20f;
    [SerializeField] private float magicCastStaminaCost = 15f;
    [SerializeField] private float chaseStaminaDrainPerSecond = 5f;
    [SerializeField] private float blockStaminaCost = 15f;

    [Header("Block")]
    [Range(0f, 1f)]
    [SerializeField] private float blockChance = 0.5f;

    [Header("Dizzy")]
    [SerializeField] private float dizzyDuration = 3f;

    [Header("Wall-Strike Stun")]
    [SerializeField] private float wallStuckDuration = 1f;    // how long he stays vulnerable after crashing into a wall
    private bool isStuckInWall = false;

    [Header("Strike Dash")]
    [SerializeField] private float strikeWindup = 0.25f;       // pause before dashing (match the anticipation frames)
    [SerializeField] private float strikeDashSpeed = 14f;      // how swift the dash is
    [SerializeField] private float strikeMaxDashTime = 0.6f;   // safety cap so he never dashes forever
    [SerializeField] private float strikeRecovery = 0.5f;      // pause after the dash before he can act
    [SerializeField] private Vector2 strikeHitOffset = new Vector2(0.9f, 0.2f);
    [SerializeField] private Vector2 strikeHitSize = new Vector2(0.8f, 1.2f);
    private bool isDizzy = false;

    [Header("Platforms / Teleport")]
    [SerializeField] private AurethPlatform[] platforms;       // element 0 = the platform he starts on
    [SerializeField] private float teleportHealPercent = 0.08f; // heal ~8% of max health per teleport
    [SerializeField] private int maxTeleportsPerPhase = 2;
    [SerializeField] private float minTeleportThreshold = 5f;   // damage needed before a teleport (random between min and max)
    [SerializeField] private float maxTeleportThreshold = 10f;
    [SerializeField] private float teleportVanishTime = 0.3f;
    [SerializeField] private float teleportAppearTime = 0.3f;
    [SerializeField] private GameObject teleportVFXPrefab;
    private int currentPlatformIndex = 0;
    private int teleportsUsedThisPhase = 0;
    private float damageSinceLastTeleport = 0f;
    private float nextTeleportThreshold;
    private float lastKnownHealth;
    private bool isTeleporting = false;

    private int phase = 1;

    private bool busy = false;
    private float decisionTimer = 0f;

    public bool IsDizzy => isDizzy;
    public bool IsStuckInWall => isStuckInWall;
    public bool IsTeleporting => isTeleporting;
    public float CurrentStamina => currentStamina;
    public float MaxStamina => maxStamina;

    private void Awake()
    {
        if (anim == null) anim = GetComponent<Animator>();
        if (health == null) health = GetComponent<Health>();
        if (rb == null) rb = GetComponent<Rigidbody2D>();
        currentStamina = maxStamina;
        initScale = transform.localScale;

        RollNextTeleportThreshold();

        if (platforms != null && platforms.Length > 0)
            ApplyPlatformEdges(currentPlatformIndex);
    }

    private void Start()
    {
        lastKnownHealth = health.currentHealth;
    }

    private void Update()
    {
        if (isDizzy || busy || isStuckInWall || isTeleporting) return;

        // Teleport check comes first; if it fires, skip the rest of this frame
        if (CheckDamageForTeleport()) return;
        CheckPhase();

        if (!IsPlayerBetweenEdges())
        {
            Patrol();
            return;
        }

        DrainStamina(chaseStaminaDrainPerSecond * Time.deltaTime);

        float distance = Vector2.Distance(transform.position, player.position);
        FaceTarget();
        decisionTimer -= Time.deltaTime;

        // Melee is now decided by the heavy box (the magenta box you see in Scene view)
        bool inMeleeRange = Physics2D.OverlapBox(BoxCenter(heavyBoxOffset), heavyBoxSize, 0f, playerLayer) != null;
        bool inMagicRange = distance <= magicRange && magicEnabled;

        if (!inMeleeRange && !inMagicRange)
        {
            anim.SetBool("isWalking", true);
            float dir = Mathf.Sign(player.position.x - transform.position.x);
            rb.linearVelocity = new Vector2(dir * moveSpeed, rb.linearVelocity.y);
        }
        else
        {
            anim.SetBool("isWalking", false);
            rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);

            if (decisionTimer <= 0f)
            {
                decisionTimer = decisionCooldown;
                ChooseAttack(inMeleeRange, inMagicRange);
            }
        }
    }

    // ---------- Phases ----------

    private void CheckPhase()
    {
        float healthPercent = health.currentHealth / health.MaxHealth;
        int newPhase;

        if (healthPercent <= 0.33f) newPhase = 3;
        else if (healthPercent <= 0.66f) newPhase = 2;
        else newPhase = 1;

        if (newPhase != phase)
        {
            phase = newPhase;
            teleportsUsedThisPhase = 0; // fresh teleport allowance for the new phase

            decisionCooldown = phase == 3 ? 0.8f : (phase == 2 ? 1.0f : 1.2f);
        }
    }

    // ---------- Teleport ----------

    private void RollNextTeleportThreshold()
    {
        nextTeleportThreshold = Random.Range(minTeleportThreshold, maxTeleportThreshold);
    }

    private void ApplyPlatformEdges(int index)
    {
        leftEdge = platforms[index].leftEdge;
        rightEdge = platforms[index].rightEdge;
    }

    // Returns true if a teleport was started
    private bool CheckDamageForTeleport()
    {
        float damageTaken = lastKnownHealth - health.currentHealth;

        if (damageTaken > 0f)
        {
            damageSinceLastTeleport += damageTaken;
            lastKnownHealth = health.currentHealth;
        }
        else if (damageTaken < 0f)
        {
            // healed (or reset): just resync so it isn't counted as damage later
            lastKnownHealth = health.currentHealth;
        }

        if (damageSinceLastTeleport >= nextTeleportThreshold
            && teleportsUsedThisPhase < maxTeleportsPerPhase
            && platforms != null && platforms.Length > 1)
        {
            StartCoroutine(DoTeleport());
            return true;
        }

        return false;
    }

    private IEnumerator DoTeleport()
    {
        isTeleporting = true;
        busy = true;
        rb.linearVelocity = Vector2.zero;
        anim.SetBool("isWalking", false);

        if (teleportVFXPrefab != null)
            Instantiate(teleportVFXPrefab, transform.position, Quaternion.identity);

        // Placeholder vanish cue (reuses the dizzy pose); swap for a dedicated teleport animation later
        if (anim != null) anim.SetBool("isDizzy", true);
        yield return new WaitForSeconds(teleportVanishTime);

        // Pick a new platform, never the one he's currently on
        int newIndex = currentPlatformIndex;
        do { newIndex = Random.Range(0, platforms.Length); }
        while (newIndex == currentPlatformIndex);
        currentPlatformIndex = newIndex;

        transform.position = platforms[currentPlatformIndex].spawnPoint.position;
        rb.linearVelocity = Vector2.zero;
        ApplyPlatformEdges(currentPlatformIndex);

        if (teleportVFXPrefab != null)
            Instantiate(teleportVFXPrefab, transform.position, Quaternion.identity);

        yield return new WaitForSeconds(teleportAppearTime);

        if (anim != null && !isDizzy) anim.SetBool("isDizzy", false);

        // Reset stamina, heal, reset damage counter
        currentStamina = maxStamina;
        health.AddHealth(health.MaxHealth * teleportHealPercent);
        damageSinceLastTeleport = 0f;
        lastKnownHealth = health.currentHealth;
        teleportsUsedThisPhase++;
        RollNextTeleportThreshold();

        isTeleporting = false;
        busy = false;
    }

    // ---------- Patrol ----------

    private void Patrol()
    {
        if (leftEdge == null || rightEdge == null) return;

        if (movingLeft)
        {
            if (transform.position.x >= leftEdge.position.x)
                MoveInDirection(-1);
            else
                PatrolDirectionChange();
        }
        else
        {
            if (transform.position.x <= rightEdge.position.x)
                MoveInDirection(1);
            else
                PatrolDirectionChange();
        }
    }

    private void PatrolDirectionChange()
    {
        anim.SetBool("isWalking", false);
        rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
        idleTimer += Time.deltaTime;

        if (idleTimer > patrolIdleDuration)
            movingLeft = !movingLeft;
    }

    private void MoveInDirection(int _direction)
    {
        idleTimer = 0;
        anim.SetBool("isWalking", true);

        transform.localScale = new Vector3(Mathf.Abs(initScale.x) * _direction,
            initScale.y, initScale.z);

        rb.linearVelocity = new Vector2(_direction * patrolSpeed, rb.linearVelocity.y);
    }

    private bool IsPlayerBetweenEdges()
    {
        if (leftEdge == null || rightEdge == null) return true;
        return player.position.x >= leftEdge.position.x && player.position.x <= rightEdge.position.x;
    }

    private void FaceTarget()
    {
        float dir = Mathf.Sign(player.position.x - transform.position.x);
        transform.localScale = new Vector3(Mathf.Abs(initScale.x) * dir,
            initScale.y, initScale.z);
    }

    // ---------- Attacks ----------

    private void ChooseAttack(bool inMeleeRange, bool inMagicRange)
    {
        if (inMeleeRange)
        {
            // Player is inside the magenta box: small attack by default
            if (Random.value < heavyChanceInMelee)
                StartCoroutine(DoHeavyAttack());
            else
                StartCoroutine(DoLightAttack());
        }
        else if (inMagicRange)
        {
            StartCoroutine(DoMagicCast());
        }
    }

    private IEnumerator DoLightAttack()
    {
        busy = true;
        anim.SetTrigger("attack");
        yield return new WaitForSeconds(lightAttackDuration);
        busy = false;
    }

    private IEnumerator DoHeavyAttack()
    {
        busy = true;
        anim.SetTrigger("strike");
        OnHeavyAttackPerformed();

        // 1. Windup: he stays still, so the player has time to dodge
        rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
        yield return new WaitForSeconds(strikeWindup);

        // 2. Dash until the end of the magenta box
        float facing = Mathf.Sign(transform.localScale.x);
        float startX = transform.position.x;
        float dashDistance = heavyBoxOffset.x + heavyBoxSize.x * 0.5f;
        bool hitPlayer = false;
        bool crashedIntoWall = false;
        float timer = 0f;

        while (Mathf.Abs(transform.position.x - startX) < dashDistance && timer < strikeMaxDashTime)
        {
            rb.linearVelocity = new Vector2(facing * strikeDashSpeed, rb.linearVelocity.y);

            Vector2 center = BoxCenter(strikeHitOffset);

            // Damage the player once per dash
            if (!hitPlayer)
            {
                Collider2D p = Physics2D.OverlapBox(center, strikeHitSize, 0f, playerLayer);
                if (p != null)
                {
                    p.GetComponent<Health>()?.TakeDamage(heavyDamage);
                    hitPlayer = true;
                }
            }

            // Crashed into a wall or pillar: count it, stop the dash, get stuck
            Collider2D wall = Physics2D.OverlapBox(center, strikeHitSize, 0f, wallLayer);
            if (wall != null)
            {
                wall.GetComponent<DestructibleWall>()?.OnHeavyAttackHit();
                crashedIntoWall = true;
                break;
            }

            // Don't dash off the platform edges
            if (leftEdge != null && rightEdge != null)
            {
                if (facing < 0 && transform.position.x <= leftEdge.position.x) break;
                if (facing > 0 && transform.position.x >= rightEdge.position.x) break;
            }

            timer += Time.fixedDeltaTime;
            yield return new WaitForFixedUpdate();
        }

        // 3. Stop, then either get stuck in the wall or recover normally
        rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);

        if (crashedIntoWall)
        {
            // StuckInWall releases "busy" when it finishes
            yield return StartCoroutine(StuckInWall());
            yield break;
        }

        yield return new WaitForSeconds(strikeRecovery);
        busy = false;
    }

    private IEnumerator StuckInWall()
    {
        isStuckInWall = true;
        rb.linearVelocity = Vector2.zero;

        if (anim != null) anim.SetBool("isDizzy", true);

        yield return new WaitForSeconds(wallStuckDuration);

        isStuckInWall = false;
        if (anim != null && !isDizzy) anim.SetBool("isDizzy", false);
        busy = false;
    }

    private IEnumerator DoMagicCast()
    {
        busy = true;
        pendingHeavyMagic = Random.value < 0.5f;

        anim.SetTrigger("cast");
        OnMagicCastPerformed();

        yield return new WaitForSeconds(castDuration);
        busy = false;
    }

    // ---------- Hit boxes (called by Animation Events) ----------

    private Vector2 BoxCenter(Vector2 offset)
    {
        float facing = Mathf.Sign(transform.localScale.x);
        return (Vector2)transform.position + new Vector2(offset.x * facing, offset.y);
    }

    public void LightAttackHit()
    {
        Collider2D hit = Physics2D.OverlapBox(BoxCenter(lightBoxOffset), lightBoxSize, 0f, playerLayer);
        if (hit != null)
            hit.GetComponent<Health>()?.TakeDamage(lightDamage);
    }

    public void HeavyAttackHit()
    {
        Vector2 center = BoxCenter(heavyBoxOffset);

        Collider2D hit = Physics2D.OverlapBox(center, heavyBoxSize, 0f, playerLayer);
        if (hit != null)
            hit.GetComponent<Health>()?.TakeDamage(heavyDamage);

        // Same box doubles as the wall check (missed the player, hit a wall/pillar)
        Collider2D wallHit = Physics2D.OverlapBox(center, heavyBoxSize, 0f, wallLayer);
        if (wallHit != null)
            wallHit.GetComponent<DestructibleWall>()?.OnHeavyAttackHit();
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(BoxCenter(lightBoxOffset), lightBoxSize);

        Gizmos.color = Color.magenta;
        Gizmos.DrawWireCube(BoxCenter(heavyBoxOffset), heavyBoxSize);

        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(BoxCenter(strikeHitOffset), strikeHitSize);
    }

    // ---------- Stamina / Block / Dizzy ----------

    public void OnHeavyAttackPerformed() => DrainStamina(heavyAttackStaminaCost);
    public void OnMagicCastPerformed() => DrainStamina(magicCastStaminaCost);

    public void ReceiveBoulderHit(float damage)
    {
        // Guaranteed hit while dizzy or stuck in a wall
        if (isDizzy || isStuckInWall)
        {
            health.TakeDamage(damage);
            return;
        }

        if (Random.value <= blockChance)
        {
            DrainStamina(blockStaminaCost);
            if (anim != null) anim.SetTrigger("block");
        }
        else
        {
            health.TakeDamage(damage);
        }
    }

    private void DrainStamina(float amount)
    {
        if (isDizzy) return;
        currentStamina = Mathf.Max(0f, currentStamina - amount);
        if (currentStamina <= 0f)
            StartCoroutine(EnterDizzy());
    }

    private IEnumerator EnterDizzy()
    {
        isDizzy = true;
        busy = false;
        rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);

        if (anim != null) anim.SetBool("isDizzy", true);
        yield return new WaitForSeconds(dizzyDuration);
        isDizzy = false;
        if (anim != null) anim.SetBool("isDizzy", false);
        currentStamina = maxStamina;
    }

    // Animation Event on the release frame of the Cast clip
    public void SpawnMagicProjectile()
    {
        GameObject prefabToUse = pendingHeavyMagic ? heavyMagicPrefab : lightMagicPrefab;

        if (prefabToUse != null && castPoint != null)
        {
            GameObject proj = Instantiate(prefabToUse, castPoint.position, Quaternion.identity);
            float dir = Mathf.Sign(transform.localScale.x);
            proj.GetComponent<MagicProjectile>()?.Launch(dir);
        }
    }

    public void ResetStamina() => currentStamina = maxStamina;
}