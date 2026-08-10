-- The schema analyzer applies the DDL in version order and preserves this text
-- for the forward-only runtime migration.
ALTER TABLE app.users ADD COLUMN display_name varchar(120) NULL;
INSERT INTO app.users (id, email, display_name) VALUES (0, 'seed@example.test', 'seed');
