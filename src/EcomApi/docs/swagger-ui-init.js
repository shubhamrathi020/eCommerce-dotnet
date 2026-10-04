
window.onload = function() {
  // Build a system
  let url = window.location.search.match(/url=([^&]+)/);
  if (url && url.length > 1) {
    url = decodeURIComponent(url[1]);
  } else {
    url = window.location.origin;
  }
  let options = {
  "swaggerDoc": {
    "openapi": "3.0.0",
    "paths": {
      "/auth/register": {
        "post": {
          "operationId": "AuthController_register",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/RegisterDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          },
          "tags": [
            "auth"
          ]
        }
      },
      "/auth/login": {
        "post": {
          "operationId": "AuthController_login",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/LoginDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "auth"
          ]
        }
      },
      "/auth/refresh": {
        "post": {
          "operationId": "AuthController_refresh",
          "parameters": [
            {
              "name": "x-csrf",
              "in": "header",
              "description": "Must be \"1\" (CSRF protection for cookie-authenticated calls)",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "auth"
          ]
        }
      },
      "/auth/logout": {
        "post": {
          "operationId": "AuthController_logout",
          "parameters": [
            {
              "name": "x-csrf",
              "in": "header",
              "description": "Must be \"1\"",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "204": {
              "description": ""
            }
          },
          "tags": [
            "auth"
          ]
        }
      },
      "/auth/me": {
        "get": {
          "operationId": "AuthController_me",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "auth"
          ]
        }
      },
      "/auth/verify-email": {
        "post": {
          "operationId": "AuthController_verifyEmail",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/TokenDto"
                }
              }
            }
          },
          "responses": {
            "204": {
              "description": ""
            }
          },
          "tags": [
            "auth"
          ]
        }
      },
      "/auth/password-reset/request": {
        "post": {
          "operationId": "AuthController_requestReset",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/EmailDto"
                }
              }
            }
          },
          "responses": {
            "204": {
              "description": ""
            }
          },
          "tags": [
            "auth"
          ]
        }
      },
      "/auth/password-reset": {
        "post": {
          "operationId": "AuthController_resetPassword",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/ResetPasswordDto"
                }
              }
            }
          },
          "responses": {
            "204": {
              "description": ""
            }
          },
          "tags": [
            "auth"
          ]
        }
      },
      "/account/profile": {
        "patch": {
          "operationId": "AccountsController_updateProfile",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/ProfileDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "account"
          ]
        }
      },
      "/auth/change-password": {
        "post": {
          "operationId": "AccountsController_changePassword",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/ChangePasswordDto"
                }
              }
            }
          },
          "responses": {
            "204": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "account"
          ]
        }
      },
      "/account/export": {
        "get": {
          "operationId": "AccountsController_exportData",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "account"
          ]
        }
      },
      "/account/delete": {
        "post": {
          "operationId": "AccountsController_deleteAccount",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/PasswordDto"
                }
              }
            }
          },
          "responses": {
            "204": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "account"
          ]
        }
      },
      "/account/addresses": {
        "get": {
          "operationId": "AddressesController_list",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "addresses"
          ]
        },
        "post": {
          "operationId": "AddressesController_add",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/AddressInputDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "addresses"
          ]
        }
      },
      "/account/addresses/{id}": {
        "put": {
          "operationId": "AddressesController_update",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/AddressInputDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "addresses"
          ]
        },
        "delete": {
          "operationId": "AddressesController_remove",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "addresses"
          ]
        }
      },
      "/account/addresses/{id}/default": {
        "post": {
          "operationId": "AddressesController_setDefault",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "addresses"
          ]
        }
      },
      "/catalog/home": {
        "get": {
          "operationId": "CatalogController_home",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/categories/tree": {
        "get": {
          "operationId": "CatalogController_categoryTree",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/listing": {
        "get": {
          "operationId": "CatalogController_listing",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/products/{slug}": {
        "get": {
          "operationId": "CatalogController_product",
          "parameters": [
            {
              "name": "slug",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/products/by-ids": {
        "post": {
          "operationId": "CatalogController_productsByIds",
          "parameters": [],
          "responses": {
            "201": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/products/summaries": {
        "post": {
          "operationId": "CatalogController_summariesByIds",
          "parameters": [],
          "responses": {
            "201": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/products/{id}/related": {
        "get": {
          "operationId": "CatalogController_related",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/products/{id}/bought-together": {
        "get": {
          "operationId": "CatalogController_boughtTogether",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/products/{id}/reviews": {
        "get": {
          "operationId": "CatalogController_reviews",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/catalog/serviceability/{pincode}": {
        "get": {
          "operationId": "CatalogController_serviceability",
          "parameters": [
            {
              "name": "pincode",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "catalog"
          ]
        }
      },
      "/search/suggest": {
        "get": {
          "operationId": "SearchController_suggest",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "search"
          ]
        }
      },
      "/search/popular": {
        "get": {
          "operationId": "SearchController_popular",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "search"
          ]
        }
      },
      "/admin/products": {
        "get": {
          "operationId": "AdminCatalogController_list",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-catalog"
          ]
        },
        "post": {
          "operationId": "AdminCatalogController_create",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/AdminProductInputDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-catalog"
          ]
        }
      },
      "/admin/products/categories": {
        "get": {
          "operationId": "AdminCatalogController_categories",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-catalog"
          ]
        }
      },
      "/admin/products/{id}": {
        "get": {
          "operationId": "AdminCatalogController_get",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-catalog"
          ]
        },
        "put": {
          "operationId": "AdminCatalogController_update",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/AdminProductInputDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-catalog"
          ]
        }
      },
      "/admin/products/bulk-status": {
        "patch": {
          "operationId": "AdminCatalogController_bulkSetStatus",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/BulkStatusDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-catalog"
          ]
        }
      },
      "/admin/products/bulk-delete-drafts": {
        "patch": {
          "operationId": "AdminCatalogController_bulkDeleteDrafts",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/BulkIdsDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-catalog"
          ]
        }
      },
      "/cart": {
        "get": {
          "operationId": "CartController_get",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "cart"
          ]
        },
        "delete": {
          "operationId": "CartController_clear",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "cart"
          ]
        }
      },
      "/cart/items": {
        "post": {
          "operationId": "CartController_add",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/AddToCartDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          },
          "tags": [
            "cart"
          ]
        }
      },
      "/cart/items/{variantId}": {
        "put": {
          "operationId": "CartController_setQuantity",
          "parameters": [
            {
              "name": "variantId",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/SetQuantityDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "cart"
          ]
        },
        "delete": {
          "operationId": "CartController_remove",
          "parameters": [
            {
              "name": "variantId",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "cart"
          ]
        }
      },
      "/cart/coupon": {
        "post": {
          "operationId": "CartController_applyCoupon",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/ApplyCouponDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          },
          "tags": [
            "cart"
          ]
        },
        "delete": {
          "operationId": "CartController_removeCoupon",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "cart"
          ]
        }
      },
      "/cart/shipping-method": {
        "put": {
          "operationId": "CartController_setShippingMethod",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/SetShippingDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "cart"
          ]
        }
      },
      "/checkout/shipping-options/{pincode}": {
        "get": {
          "operationId": "CheckoutController_shippingOptions",
          "parameters": [
            {
              "name": "pincode",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "checkout"
          ]
        }
      },
      "/checkout/payment-options/{pincode}": {
        "get": {
          "operationId": "CheckoutController_paymentOptions",
          "parameters": [
            {
              "name": "pincode",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "checkout"
          ]
        }
      },
      "/orders": {
        "post": {
          "operationId": "OrderController_place",
          "parameters": [],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/PlaceOrderDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          },
          "tags": [
            "orders"
          ]
        },
        "get": {
          "operationId": "OrderController_list",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "orders"
          ]
        }
      },
      "/orders/{id}": {
        "get": {
          "operationId": "OrderController_get",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "orders"
          ]
        }
      },
      "/orders/{id}/stream": {
        "get": {
          "operationId": "OrderController_stream",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "orders"
          ]
        }
      },
      "/orders/{id}/cancel": {
        "post": {
          "operationId": "OrderController_cancel",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "orders"
          ]
        }
      },
      "/orders/{id}/payment/initiate": {
        "post": {
          "operationId": "PaymentController_initiate",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "201": {
              "description": ""
            }
          },
          "tags": [
            "payments"
          ]
        }
      },
      "/orders/{id}/payment/confirm": {
        "post": {
          "operationId": "PaymentController_confirm",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/ConfirmPaymentDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          },
          "tags": [
            "payments"
          ]
        }
      },
      "/orders/{id}/payment/fail": {
        "post": {
          "operationId": "PaymentController_fail",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/FailPaymentDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "tags": [
            "payments"
          ]
        }
      },
      "/admin/orders": {
        "get": {
          "operationId": "AdminOrderController_list",
          "parameters": [],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-orders"
          ]
        }
      },
      "/admin/orders/{id}": {
        "get": {
          "operationId": "AdminOrderController_get",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-orders"
          ]
        }
      },
      "/admin/orders/{id}/status": {
        "patch": {
          "operationId": "AdminOrderController_advance",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/AdvanceOrderDto"
                }
              }
            }
          },
          "responses": {
            "200": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-orders"
          ]
        }
      },
      "/admin/orders/{id}/notes": {
        "post": {
          "operationId": "AdminOrderController_addNote",
          "parameters": [
            {
              "name": "id",
              "required": true,
              "in": "path",
              "schema": {
                "type": "string"
              }
            }
          ],
          "requestBody": {
            "required": true,
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/AddNoteDto"
                }
              }
            }
          },
          "responses": {
            "201": {
              "description": ""
            }
          },
          "security": [
            {
              "bearer": []
            }
          ],
          "tags": [
            "admin-orders"
          ]
        }
      }
    },
    "info": {
      "title": "eCommerce API",
      "description": "Identity (BRD 19), catalog and search (BRD 20), cart/checkout/orders/payments (BRD 21). Errors use the `{ code, message, fields?, requestId? }` shape.",
      "version": "0.3",
      "contact": {}
    },
    "tags": [],
    "servers": [],
    "components": {
      "securitySchemes": {
        "bearer": {
          "scheme": "bearer",
          "bearerFormat": "JWT",
          "type": "http"
        }
      },
      "schemas": {
        "RegisterDto": {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "example": "Asha Rao"
            },
            "email": {
              "type": "string",
              "example": "asha@example.com"
            },
            "password": {
              "type": "string",
              "example": "Str0ngPass"
            }
          },
          "required": [
            "name",
            "email",
            "password"
          ]
        },
        "LoginDto": {
          "type": "object",
          "properties": {
            "email": {
              "type": "string",
              "example": "demo@shop.test"
            },
            "password": {
              "type": "string"
            }
          },
          "required": [
            "email",
            "password"
          ]
        },
        "TokenDto": {
          "type": "object",
          "properties": {
            "token": {
              "type": "string"
            }
          },
          "required": [
            "token"
          ]
        },
        "EmailDto": {
          "type": "object",
          "properties": {
            "email": {
              "type": "string"
            }
          },
          "required": [
            "email"
          ]
        },
        "ResetPasswordDto": {
          "type": "object",
          "properties": {
            "token": {
              "type": "string"
            },
            "password": {
              "type": "string"
            }
          },
          "required": [
            "token",
            "password"
          ]
        },
        "ProfileDto": {
          "type": "object",
          "properties": {
            "name": {
              "type": "string"
            },
            "phone": {
              "type": "string"
            }
          },
          "required": [
            "name"
          ]
        },
        "ChangePasswordDto": {
          "type": "object",
          "properties": {
            "currentPassword": {
              "type": "string"
            },
            "newPassword": {
              "type": "string"
            }
          },
          "required": [
            "currentPassword",
            "newPassword"
          ]
        },
        "PasswordDto": {
          "type": "object",
          "properties": {
            "password": {
              "type": "string"
            }
          },
          "required": [
            "password"
          ]
        },
        "AddressDto": {
          "type": "object",
          "properties": {
            "line1": {
              "type": "string"
            },
            "line2": {
              "type": "string"
            },
            "city": {
              "type": "string"
            },
            "state": {
              "type": "string"
            },
            "pincode": {
              "type": "string"
            }
          },
          "required": [
            "line1",
            "city",
            "state",
            "pincode"
          ]
        },
        "AddressInputDto": {
          "type": "object",
          "properties": {
            "label": {
              "type": "string"
            },
            "name": {
              "type": "string"
            },
            "phone": {
              "type": "string"
            },
            "address": {
              "$ref": "#/components/schemas/AddressDto"
            }
          },
          "required": [
            "label",
            "name",
            "phone",
            "address"
          ]
        },
        "AdminVariantInputDto": {
          "type": "object",
          "properties": {
            "id": {
              "type": "string"
            },
            "sku": {
              "type": "string"
            },
            "options": {
              "type": "object"
            },
            "price": {
              "type": "number"
            },
            "mrp": {
              "type": "number"
            },
            "stock": {
              "type": "number"
            }
          },
          "required": [
            "sku",
            "options",
            "price",
            "stock"
          ]
        },
        "AdminProductInputDto": {
          "type": "object",
          "properties": {
            "title": {
              "type": "string"
            },
            "brandName": {
              "type": "string"
            },
            "categoryId": {
              "type": "string"
            },
            "description": {
              "type": "string"
            },
            "highlights": {
              "type": "array",
              "items": {
                "type": "string"
              }
            },
            "tags": {
              "type": "array",
              "items": {
                "type": "string"
              }
            },
            "status": {
              "type": "string",
              "enum": [
                "draft",
                "published",
                "archived"
              ]
            },
            "variants": {
              "type": "array",
              "items": {
                "$ref": "#/components/schemas/AdminVariantInputDto"
              }
            }
          },
          "required": [
            "title",
            "brandName",
            "categoryId",
            "description",
            "highlights",
            "tags",
            "status",
            "variants"
          ]
        },
        "BulkStatusDto": {
          "type": "object",
          "properties": {
            "ids": {
              "type": "array",
              "items": {
                "type": "string"
              }
            },
            "status": {
              "type": "string",
              "enum": [
                "draft",
                "published",
                "archived"
              ]
            }
          },
          "required": [
            "ids",
            "status"
          ]
        },
        "BulkIdsDto": {
          "type": "object",
          "properties": {
            "ids": {
              "type": "array",
              "items": {
                "type": "string"
              }
            }
          },
          "required": [
            "ids"
          ]
        },
        "AddToCartDto": {
          "type": "object",
          "properties": {}
        },
        "SetQuantityDto": {
          "type": "object",
          "properties": {}
        },
        "ApplyCouponDto": {
          "type": "object",
          "properties": {}
        },
        "SetShippingDto": {
          "type": "object",
          "properties": {}
        },
        "PlaceOrderDto": {
          "type": "object",
          "properties": {}
        },
        "ConfirmPaymentDto": {
          "type": "object",
          "properties": {}
        },
        "FailPaymentDto": {
          "type": "object",
          "properties": {}
        },
        "AdvanceOrderDto": {
          "type": "object",
          "properties": {
            "status": {
              "type": "string",
              "enum": [
                "pending_payment",
                "confirmed",
                "packed",
                "shipped",
                "delivered",
                "cancelled"
              ]
            }
          },
          "required": [
            "status"
          ]
        },
        "AddNoteDto": {
          "type": "object",
          "properties": {
            "text": {
              "type": "string"
            }
          },
          "required": [
            "text"
          ]
        }
      }
    }
  },
  "customOptions": {}
};
  url = options.swaggerUrl || url
  let urls = options.swaggerUrls
  let customOptions = options.customOptions
  let spec1 = options.swaggerDoc
  let swaggerOptions = {
    spec: spec1,
    url: url,
    urls: urls,
    dom_id: '#swagger-ui',
    deepLinking: true,
    presets: [
      SwaggerUIBundle.presets.apis,
      SwaggerUIStandalonePreset
    ],
    plugins: [
      SwaggerUIBundle.plugins.DownloadUrl
    ],
    layout: "StandaloneLayout"
  }
  for (let attrname in customOptions) {
    swaggerOptions[attrname] = customOptions[attrname];
  }
  let ui = SwaggerUIBundle(swaggerOptions)

  if (customOptions.initOAuth) {
    ui.initOAuth(customOptions.initOAuth)
  }

  if (customOptions.authAction) {
    ui.authActions.authorize(customOptions.authAction)
  }
  
  window.ui = ui
}
