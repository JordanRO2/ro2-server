using Npgsql;

namespace RO2.Persistence.Postgres;

/// <summary>
/// DDL for the tables used by the Postgres provider's repositories. Statements are idempotent
/// (<c>CREATE TABLE IF NOT EXISTS</c>) and run once, off the boot path, when the Postgres provider is selected.
/// Each repository owns one entry; entries are added as aggregates are ported.
/// </summary>
public static class PostgresSchema
{
 /// <summary>The ordered DDL statements for every ported table, each idempotent.</summary>
    public static readonly IReadOnlyList<string> Statements = new[]
    {
        
        
        """
        CREATE TABLE IF NOT EXISTS zone_auth_tokens (
            token       uuid        PRIMARY KEY,
            account_id  bigint      NOT NULL,
            expires_at  timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS cash_mall_accounts (
            account_id  bigint      PRIMARY KEY,
            balance     bigint      NOT NULL DEFAULT 0,
            updated_at  timestamptz NOT NULL DEFAULT now()
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS cash_mall_entries (
            seq          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            account_id   bigint      NOT NULL REFERENCES cash_mall_accounts (account_id) ON DELETE CASCADE,
            entry_id     bigint      NOT NULL,
            box          smallint    NOT NULL,
            item_id      bigint      NOT NULL,
            units        integer     NOT NULL,
            filed_utc    timestamptz NOT NULL,
            sender_name  text        NOT NULL DEFAULT ''
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_cash_mall_entries_account_entry ON cash_mall_entries (account_id, entry_id)
        """,
        """
        CREATE TABLE IF NOT EXISTS cash_mall_purchases (
            seq                   bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            account_id            bigint      NOT NULL REFERENCES cash_mall_accounts (account_id) ON DELETE CASCADE,
            order_id              bigint      NOT NULL,
            item_id               bigint      NOT NULL,
            product_id            bigint      NOT NULL,
            quantity              bigint      NOT NULL,
            unit_price            bigint      NOT NULL,
            bought_utc            timestamptz NOT NULL,
            gift                  boolean     NOT NULL,
            sender_name           text        NOT NULL DEFAULT '',
            recipient_name        text        NOT NULL DEFAULT '',
            recipient_account_id  bigint      NOT NULL
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_cash_mall_purchases_account_order ON cash_mall_purchases (account_id, order_id)
        """,
        """
        CREATE TABLE IF NOT EXISTS cash_mall_counters (
            counter_name  text   PRIMARY KEY,
            seq           bigint NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS mail_counters (
            id   text   PRIMARY KEY,
            seq  bigint NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS mail_messages (
            id                                  bigint      PRIMARY KEY,
            uid                                 bigint      NOT NULL,
            receiver_char_id                    bigint      NOT NULL,
            receiver_name                       text        NOT NULL,
            sender_char_id                      bigint      NOT NULL,
            sender_name                         text        NOT NULL,
            title                               text        NOT NULL,
            body                                text        NOT NULL,
            attached_item_uid                   bigint      NOT NULL,
            attached_item_id                    bigint      NOT NULL,
            attached_item_count                 integer     NOT NULL,
            attached_item_refine                smallint    NOT NULL,
            attached_item_durability            smallint    NOT NULL,
            attached_item_cards_json            text        NULL,
            attached_item_element               smallint    NOT NULL,
            attached_item_dye_color             bigint      NOT NULL,
            attached_item_socket_punchers_json  text        NULL,
            attached_item_dye_color2            bigint      NOT NULL,
            attached_item_dye_color3            bigint      NOT NULL,
            attached_item_fx_enchant_index      bigint      NOT NULL,
            attached_item_enchant_reinforce_id  bigint      NOT NULL,
            attached_item_enchant_seconds       bigint      NOT NULL,
            attached_item_bound                 boolean     NOT NULL,
            attached_item_ability_value         bigint      NULL,
            item_claimed                        boolean     NOT NULL,
            gold                                bigint      NOT NULL,
            gold_claimed                        boolean     NOT NULL,
            is_read                             boolean     NOT NULL,
            sent_at                             timestamptz NOT NULL,
            kind                                smallint    NOT NULL,
            kind_item_id                        bigint      NOT NULL,
            auction_price                       bigint      NOT NULL
        )
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_mail_messages_uid ON mail_messages (uid)
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_mail_messages_receiver_sent ON mail_messages (receiver_char_id, sent_at DESC)
        """,
        
        """
        CREATE TABLE IF NOT EXISTS auction_articles (
            id                   bigint       PRIMARY KEY,
            uid                  bigint       NOT NULL UNIQUE,
            seller_char_id       bigint       NOT NULL,
            state                smallint     NOT NULL,
            item_id              bigint       NOT NULL,
            start_price          bigint       NOT NULL,
            buyout_price         bigint       NOT NULL,
            top_bid              bigint       NOT NULL,
            top_bidder_char_id   bigint       NOT NULL,
            top_bidder_name      text         NOT NULL,
            top_bidder_char_uid  bigint       NOT NULL,
            started_at           timestamptz  NOT NULL,
            expires_at           timestamptz  NOT NULL,
            buyer_char_id        bigint       NOT NULL,
            closed_at            timestamptz,
            item_snapshot        jsonb        NOT NULL
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_auction_articles_seller_state ON auction_articles (seller_char_id, state)
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_auction_articles_state_item ON auction_articles (state, item_id)
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_auction_articles_state_expires ON auction_articles (state, expires_at)
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_auction_articles_topbidder_state ON auction_articles (top_bidder_char_id, state)
        """,
        """
        CREATE TABLE IF NOT EXISTS auction_counters (
            id   text   PRIMARY KEY,
            seq  bigint NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS card_books (
            character_id  bigint  PRIMARY KEY,
            cards         jsonb   NOT NULL DEFAULT '[]'::jsonb
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS card_book_charges (
            character_id       bigint      PRIMARY KEY,
            charged_until_utc  timestamptz NOT NULL,
            updated_at         timestamptz NOT NULL DEFAULT now()
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS card_registries (
            character_id  bigint      PRIMARY KEY,
            cards         jsonb       NOT NULL,
            updated_at    timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS collection_books (
            character_id             bigint   PRIMARY KEY,
            registered_monster_ids   jsonb    NOT NULL DEFAULT '[]'::jsonb,
            registered_pet_ids       jsonb    NOT NULL DEFAULT '[]'::jsonb,
            selected_monster_skills  jsonb    NOT NULL DEFAULT '[]'::jsonb,
            selected_pet_skills      jsonb    NOT NULL DEFAULT '[]'::jsonb,
            active_pet_id            bigint   NOT NULL DEFAULT 0,
            active_pet_flag          smallint NOT NULL DEFAULT 0,
            pet_loyalties            jsonb    NOT NULL DEFAULT '[]'::jsonb
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS collection_monster_progress (
            character_id  bigint  PRIMARY KEY,
            entries       jsonb   NOT NULL DEFAULT '[]'::jsonb
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_khara_title (
            character_id       bigint      PRIMARY KEY,
            khara_a            bigint      NOT NULL DEFAULT 0,
            khara_b            bigint      NOT NULL DEFAULT 0,
            a_timer_start_utc  timestamptz NULL,
            last_success_utc   timestamptz NULL,
            play_clock_utc     timestamptz NULL,
            updated_utc        timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_khara (
            character_id  bigint      PRIMARY KEY,
            points        bigint      NOT NULL DEFAULT 0,
            active        jsonb       NOT NULL DEFAULT '[]'::jsonb,
            completed     jsonb       NOT NULL DEFAULT '[]'::jsonb,
            updated_utc   timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS khara_traces (
            character_id  bigint  PRIMARY KEY,
            khara_ids     jsonb   NOT NULL DEFAULT '[]'::jsonb
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_vehicles (
            character_id  bigint      PRIMARY KEY,
            vehicles      jsonb       NOT NULL DEFAULT '[]'::jsonb,
            updated_at    timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS pet_incubators (
            character_id  bigint  PRIMARY KEY,
            slots         jsonb   NOT NULL DEFAULT '[]'::jsonb
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS account_gift_boxes (
            account_id                      bigint      PRIMARY KEY,
            attended_days                   integer     NOT NULL DEFAULT 0,
            last_attended_day               integer     NOT NULL DEFAULT 0,
            attendance_streak_broken_after  integer     NOT NULL DEFAULT 0,
            updated_at                      timestamptz NOT NULL DEFAULT now()
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS account_gift_entries (
            seq           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            account_id    bigint      NOT NULL REFERENCES account_gift_boxes (account_id) ON DELETE CASCADE,
            guid          text        NOT NULL,
            item_id       bigint      NOT NULL,
            count         integer     NOT NULL,
            received_utc  timestamptz NOT NULL,
            source        smallint    NOT NULL
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_account_gift_entries_account_guid ON account_gift_entries (account_id, guid)
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_account_gift_entries_account_received ON account_gift_entries (account_id, received_utc)
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_keyconfig (
            character_id  bigint      PRIMARY KEY,
            mode          integer     NOT NULL DEFAULT 0,
            entries       bytea       NOT NULL DEFAULT ''::bytea,
            updated_utc   timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS chat_channel_colors (
            owner_id       bigint  NOT NULL,
            channel_index  integer NOT NULL,
            argb           bigint  NOT NULL,
            PRIMARY KEY (owner_id, channel_index)
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS craft_recipes (
            seq           bigint GENERATED ALWAYS AS IDENTITY,
            character_id  bigint NOT NULL,
            recipe_id     bigint NOT NULL,
            PRIMARY KEY (character_id, recipe_id)
        )
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_craft_recipes_seq ON craft_recipes (seq)
        """,
        
        """
        CREATE TABLE IF NOT EXISTS world_event_state (
            world_event_id     bigint      PRIMARY KEY,
            stage              smallint    NOT NULL,
            stage_entered_utc  timestamptz NOT NULL,
            counts             jsonb       NOT NULL DEFAULT '[]'::jsonb,
            updated_utc        timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS present_passwords (
            account_id  bigint      PRIMARY KEY,
            hash        text        NOT NULL,
            updated_at  timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_dungeon_entries (
            character_id      bigint      PRIMARY KEY,
            window_start_utc  timestamptz NOT NULL,
            counts            jsonb       NOT NULL DEFAULT '{}'::jsonb,
            updated_utc       timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS guild_departures (
            char_id              bigint      PRIMARY KEY,
            left_utc             timestamptz NULL,
            kick_notice_pending  boolean     NOT NULL DEFAULT false
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS guild_emblems (
            id            text        PRIMARY KEY,
            emblem_index  bigint      NOT NULL,
            image         bytea       NOT NULL,
            updated_utc   timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS guild_storage_log (
            seq         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            guild_guid  bytea       NOT NULL,
            when_utc    timestamptz NOT NULL,
            actor_name  text        NOT NULL DEFAULT '',
            kind        smallint    NOT NULL,
            item_id     bigint      NOT NULL,
            count       integer     NOT NULL
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_guild_storage_log_guild_when ON guild_storage_log (guild_guid, when_utc DESC, seq DESC)
        """,
        """
        CREATE TABLE IF NOT EXISTS guild_funds_log (
            seq         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            guild_guid  bytea       NOT NULL,
            when_utc    timestamptz NOT NULL,
            actor_name  text        NOT NULL DEFAULT '',
            kind        smallint    NOT NULL,
            amount      bigint      NOT NULL
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_guild_funds_log_guild_when ON guild_funds_log (guild_guid, when_utc DESC, seq DESC)
        """,
        
        """
        CREATE TABLE IF NOT EXISTS union_memberships (
            character_id    bigint PRIMARY KEY,
            union_id        bigint NOT NULL,
            joined_at_unix  bigint NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS game_mall_list_items (
            seq         bigint GENERATED ALWAYS AS IDENTITY,
            account_id  bigint NOT NULL,
            item_id     bigint NOT NULL,
            PRIMARY KEY (account_id, item_id)
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_game_mall_list_items_account_seq ON game_mall_list_items (account_id, seq DESC)
        """,
        
        """
        CREATE TABLE IF NOT EXISTS private_shop_crafts (
            character_id  bigint      PRIMARY KEY,
            offers        jsonb       NOT NULL DEFAULT '[]'::jsonb,
            updated_at    timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_illusion_pyramid (
            character_id  bigint      PRIMARY KEY,
            mystical      integer     NOT NULL DEFAULT 0,
            bright        integer     NOT NULL DEFAULT 0,
            top_record    smallint    NOT NULL DEFAULT 0,
            updated_utc   timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS account_playtime_reward (
            account_id           bigint           PRIMARY KEY,
            slot_id              bigint           NOT NULL,
            accumulated_seconds  double precision NOT NULL,
            period_start_utc     timestamptz      NOT NULL,
            updated_utc          timestamptz      NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_quickslots (
            id            bigint   NOT NULL,
            character_id  bigint   NOT NULL,
            page          smallint NOT NULL,
            slot          smallint NOT NULL,
            entry_type    smallint NOT NULL,
            entry_id      bigint   NOT NULL,
            amount        integer  NOT NULL,
            item_uid      bigint   NOT NULL,
            PRIMARY KEY (character_id, page, slot)
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_skills (
            id            bigint   GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            character_id  bigint   NOT NULL,
            skill_id      bigint   NOT NULL,
            level         smallint NOT NULL DEFAULT 1,
            CONSTRAINT ux_character_skills_char_skill UNIQUE (character_id, skill_id)
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS character_quests (
            id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            character_id  bigint      NOT NULL,
            quest_id      bigint      NOT NULL,
            status        smallint    NOT NULL,
            progress_json text        NULL,
            is_traced     boolean     NOT NULL DEFAULT false,
            started_at    timestamptz NOT NULL,
            completed_at  timestamptz NULL
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_character_quests_char_status_started ON character_quests (character_id, status, started_at, id)
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_character_quests_char_quest_status ON character_quests (character_id, quest_id, status)
        """,
        
        """
        CREATE TABLE IF NOT EXISTS channel_presence_accounts (
            id              bigint      PRIMARY KEY,
            channel_handle  bigint      NOT NULL,
            updated_utc     timestamptz NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS channel_presence_characters (
            id              bigint      PRIMARY KEY,
            channel_handle  bigint      NOT NULL,
            location        text        NOT NULL DEFAULT '',
            updated_utc     timestamptz NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS ranking_scores (
            ranking_type      smallint    NOT NULL,
            period_start_utc  timestamptz NOT NULL,
            char_id           bigint      NOT NULL,
            score             bigint      NOT NULL,
            reached_utc       timestamptz NOT NULL,
            PRIMARY KEY (ranking_type, period_start_utc, char_id)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS ranking_snapshots (
            id                text        PRIMARY KEY,
            ranking_type      smallint    NOT NULL,
            period_start_utc  timestamptz NOT NULL,
            rolled_utc        timestamptz NOT NULL,
            board_rows        jsonb       NOT NULL DEFAULT '[]'::jsonb
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_ranking_snapshots_type_period ON ranking_snapshots (ranking_type, period_start_utc DESC)
        """,
        
        """
        CREATE TABLE IF NOT EXISTS online_accounts (
            account_id         bigint      PRIMARY KEY,
            owner              text        NOT NULL,
            seen_at            timestamptz NOT NULL,
            kick_requested_at  timestamptz NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS accounts (
            id                    bigint      PRIMARY KEY,
            username              text        UNIQUE,
            password_hash         text,
            second_password_hash  text,
            email                 text        NOT NULL DEFAULT '',
            created_at            timestamptz NOT NULL DEFAULT now(),
            last_login            timestamptz,
            is_banned             boolean     NOT NULL DEFAULT false,
            ban_expires           timestamptz,
            ban_reason            text,
            access_level          smallint    NOT NULL DEFAULT 0 CHECK (access_level BETWEEN 0 AND 255)
        )
        """,
        
        """
        CREATE SEQUENCE IF NOT EXISTS accounts_id_seq
        """,
        
        """
        CREATE TABLE IF NOT EXISTS characters (
            id                   bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            account_id           bigint      NOT NULL,
            name                 text        NOT NULL,
            job                  smallint    NOT NULL DEFAULT 0,
            race                 smallint    NOT NULL DEFAULT 2,
            life_job             smallint    NOT NULL DEFAULT 0,
            gender               smallint    NOT NULL DEFAULT 0,
            level                smallint    NOT NULL DEFAULT 1,
            exp                  bigint      NOT NULL DEFAULT 0,
            job_level            smallint    NOT NULL DEFAULT 1,
            job_exp              bigint      NOT NULL DEFAULT 0,
            master_level         bigint      NOT NULL DEFAULT 0,
            map_id               bigint      NOT NULL DEFAULT 0,
            x                    real        NOT NULL DEFAULT 0,
            y                    real        NOT NULL DEFAULT 0,
            z                    real        NOT NULL DEFAULT 0,
            hp                   integer     NOT NULL DEFAULT 0,
            max_hp               integer     NOT NULL DEFAULT 0,
            mp                   integer     NOT NULL DEFAULT 0,
            max_mp               integer     NOT NULL DEFAULT 0,
            str                  integer     NOT NULL DEFAULT 0,
            agi                  integer     NOT NULL DEFAULT 0,
            vit                  integer     NOT NULL DEFAULT 0,
            intel                integer     NOT NULL DEFAULT 0,
            wis                  integer     NOT NULL DEFAULT 0,
            luk                  integer     NOT NULL DEFAULT 0,
            bonus_str            integer     NOT NULL DEFAULT 0,
            bonus_agi            integer     NOT NULL DEFAULT 0,
            bonus_int            integer     NOT NULL DEFAULT 0,
            bonus_wis            integer     NOT NULL DEFAULT 0,
            bonus_vit            integer     NOT NULL DEFAULT 0,
            bonus_luk            integer     NOT NULL DEFAULT 0,
            gold                 bigint      NOT NULL DEFAULT 0,
            bank_gold            bigint      NOT NULL DEFAULT 0,
            bind_index           bigint      NOT NULL DEFAULT 0,
            facing_yaw           real        NOT NULL DEFAULT 0,
            appearance_json      text        NULL,
            equip_hide_flag      bigint      NOT NULL DEFAULT 6,
            allow_anyone_review  boolean     NOT NULL DEFAULT true,
            bank_slots           smallint    NOT NULL DEFAULT 20,
            created_at           timestamptz NOT NULL DEFAULT now(),
            last_played          timestamptz NULL,
            play_time_seconds    bigint      NOT NULL DEFAULT 0,
            is_deleted           boolean     NOT NULL DEFAULT false,
            deleted_at           timestamptz NULL
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_characters_account_live ON characters (account_id) WHERE NOT is_deleted
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_characters_name_live ON characters (name) WHERE NOT is_deleted
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_characters_lower_name_live ON characters (lower(name)) WHERE NOT is_deleted
        """,
        
        """
        CREATE TABLE IF NOT EXISTS inventory_items (
            id                    bigint      PRIMARY KEY,
            character_id          bigint      NOT NULL,
            item_id               bigint      NOT NULL,
            count                 integer     NOT NULL DEFAULT 1,
            slot                  smallint    NOT NULL,
            storage_type          smallint    NOT NULL,
            refine                smallint    NOT NULL DEFAULT 0,
            element               smallint    NOT NULL DEFAULT 0,
            durability            smallint    NOT NULL DEFAULT -1,
            dye_color             bigint      NOT NULL DEFAULT 0,
            dye_color2            bigint      NOT NULL DEFAULT 0,
            dye_color3            bigint      NOT NULL DEFAULT 0,
            fx_enchant_index      bigint      NOT NULL DEFAULT 0,
            enchant_reinforce_id  bigint      NOT NULL DEFAULT 0,
            enchant_seconds       bigint      NOT NULL DEFAULT 0,
            cards_json            text        NULL,
            socket_punchers_json  text        NULL,
            bound                 boolean     NOT NULL DEFAULT false,
            ability_value         bigint      NULL,
            created_at            timestamptz NOT NULL DEFAULT now()
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_inventory_items_character_storage ON inventory_items (character_id, storage_type)
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS uq_inventory_char_storage_slot ON inventory_items (character_id, storage_type, slot) WHERE slot >= 0
        """,
        """
        CREATE TABLE IF NOT EXISTS inventory_counters (
            counter_name  text   PRIMARY KEY,
            seq           bigint NOT NULL
        )
        """,
        
        """
        CREATE TABLE IF NOT EXISTS one_shot_tokens (
            token          text        PRIMARY KEY,
            purpose        text        NOT NULL,
            account_id     bigint      NOT NULL,
            character_uid  bigint      NOT NULL,
            issued_utc     timestamptz NOT NULL,
            expiry         timestamptz NOT NULL
        )
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_one_shot_tokens_expiry ON one_shot_tokens (expiry)
        """,
        
        """
        CREATE TABLE IF NOT EXISTS guilds (
            id    bigint PRIMARY KEY,
            data  jsonb  NOT NULL
        )
        """,
    };

 /// <summary>Create every ported table if it does not already exist.</summary>
    public static async Task EnsureAsync(PostgresDbContext db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        foreach (var ddl in Statements)
        {
            await using var cmd = new NpgsqlCommand(ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
