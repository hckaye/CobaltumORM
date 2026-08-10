CREATE TABLE e2e_values (
    id integer PRIMARY KEY,
    local_time timestamp without time zone NOT NULL,
    document jsonb NOT NULL,
    big_id bigint NOT NULL
);
